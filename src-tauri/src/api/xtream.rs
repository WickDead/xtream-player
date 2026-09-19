use std::collections::{HashMap, HashSet};

use base64::{engine::general_purpose::STANDARD, Engine as _};
use quick_xml::{events::Event, Reader};
use reqwest::Client;
use serde::Deserialize;
use serde_json::Value;
use tauri::State;
use thiserror::Error;
use uuid::Uuid;

use crate::{
    models::{
        BootstrapState, EpgListing, LiveChannel, ProviderPreview, ResumeCheckpointRequest,
        SaveProfileRequest, SeriesDetail, SeriesEpisode, SeriesItem, SeriesSeason, VodDetail,
        VodItem,
        XmltvSnapshot, XtreamProfile,
    },
    persistence::sqlite,
    AppState,
};

#[derive(Debug, Error)]
enum XtreamError {
    #[error("network request failed: {0}")]
    Http(#[from] reqwest::Error),
    #[error("provider returned an unexpected payload")]
    InvalidPayload,
    #[error("authentication failed for this Xtream provider")]
    AuthenticationFailed,
    #[error("persistence failed: {0}")]
    Persistence(#[from] sqlite::DatabaseError),
}

struct XtreamClient {
    base_url: String,
    username: String,
    password: String,
    client: Client,
}

impl XtreamClient {
    fn new(server_url: &str, username: &str, password: &str) -> Result<Self, XtreamError> {
        let trimmed = server_url.trim();
        let with_scheme = if trimmed.starts_with("http://") || trimmed.starts_with("https://") {
            trimmed.to_string()
        } else {
            format!("http://{trimmed}")
        };
        let base_url = with_scheme.trim_end_matches('/').to_string();
        let client = Client::builder()
            .user_agent("Mozilla/5.0 (Windows NT 10.0; Win64; x64) XtreamPlayer/0.1")
            .build()?;

        Ok(Self {
            base_url,
            username: username.to_string(),
            password: password.to_string(),
            client,
        })
    }

    async fn fetch_player_api<T>(&self, action: Option<&str>) -> Result<T, XtreamError>
    where
        T: serde::de::DeserializeOwned,
    {
        let mut query = vec![
            ("username", self.username.as_str()),
            ("password", self.password.as_str()),
        ];

        if let Some(action) = action {
            query.push(("action", action));
        }

        let response = self
            .client
            .get(format!("{}/player_api.php", self.base_url))
            .query(&query)
            .send()
            .await?
            .error_for_status()?;

        Ok(response.json::<T>().await?)
    }

    async fn authenticate(&self) -> Result<AuthPayload, XtreamError> {
        let payload = self.fetch_player_api::<AuthPayload>(None).await?;
        let is_authorized = payload
            .user_info
            .as_ref()
            .and_then(|info| info.auth.as_deref())
            .map(|flag| flag == "1")
            .unwrap_or(false);

        if is_authorized {
            eprintln!("authenticate: provider accepted credentials for {}", self.username);
            Ok(payload)
        } else {
            eprintln!("authenticate: provider rejected credentials for {}", self.username);
            Err(XtreamError::AuthenticationFailed)
        }
    }

    async fn category_map(&self, action: &str) -> Result<HashMap<String, String>, XtreamError> {
        let categories = self.fetch_player_api::<Vec<RawCategory>>(Some(action)).await?;

        Ok(categories
            .into_iter()
            .filter_map(|category| {
                let id = category.category_id?;
                let name = category.category_name.unwrap_or_else(|| "Uncategorized".into());
                Some((id, name))
            })
            .collect())
    }

    async fn ordered_category_map(
        &self,
        action: &str,
    ) -> Result<HashMap<String, (String, i64)>, XtreamError> {
        let categories = self.fetch_player_api::<Vec<RawCategory>>(Some(action)).await?;

        Ok(categories
            .into_iter()
            .enumerate()
            .filter_map(|(index, category)| {
                let id = category.category_id?;
                let name = category.category_name.unwrap_or_else(|| "Uncategorized".into());
                Some((id, (name, index as i64)))
            })
            .collect())
    }

    async fn live_catalog(&self) -> Result<Vec<LiveChannel>, XtreamError> {
        let categories = self
            .ordered_category_map("get_live_categories")
            .await
            .unwrap_or_default();
        let auth = self.authenticate().await?;
        let allowed_formats = auth
            .user_info
            .and_then(|info| info.allowed_output_formats)
            .unwrap_or_else(|| vec!["ts".into()]);
        let preferred_ext = allowed_formats
            .into_iter()
            .find(|value| value == "ts" || value == "m3u8")
            .unwrap_or_else(|| "ts".into());

        let streams = self
            .fetch_player_api::<Vec<RawLiveStream>>(Some("get_live_streams"))
            .await?;

        Ok(streams
            .into_iter()
            .map(|stream| {
                let category_entry = stream
                    .category_id
                    .as_ref()
                    .and_then(|id| categories.get(id));

                LiveChannel {
                    id: stream.stream_id.clone().unwrap_or_else(|| Uuid::new_v4().to_string()),
                    name: stream.name.unwrap_or_else(|| "Untitled live stream".into()),
                    category: category_entry
                        .map(|(name, _)| name.clone())
                        .unwrap_or_else(|| "Uncategorized".into()),
                    category_order: category_entry.map(|(_, order)| *order),
                    epg_channel_id: stream.epg_channel_id,
                    current_program: "Use short EPG sync for now/next".into(),
                    next_program: "Full XMLTV parsing is planned next".into(),
                    bitrate_label: stream
                        .bitrate
                        .map(|value| format!("{} kbps", value))
                        .unwrap_or_else(|| "Unknown bitrate".into()),
                    stream_url: format!(
                        "{}/live/{}/{}/{}.{}",
                        self.base_url,
                        self.username,
                        self.password,
                        stream.stream_id.unwrap_or_else(|| "0".into()),
                        preferred_ext
                    ),
                }
            })
            .collect())
    }

    async fn short_epg(&self, stream_id: &str, limit: usize) -> Result<Vec<EpgListing>, XtreamError> {
        let payload = self
            .client
            .get(format!("{}/player_api.php", self.base_url))
            .query(&[
                ("username", self.username.as_str()),
                ("password", self.password.as_str()),
                ("action", "get_short_epg"),
                ("stream_id", stream_id),
                ("limit", &limit.to_string()),
            ])
            .send()
            .await?;
        let payload = payload.error_for_status()?.json::<RawShortEpgPayload>().await?;

        Ok(payload
            .epg_listings
            .into_iter()
            .map(|entry| EpgListing {
                id: entry.id.unwrap_or_else(|| Uuid::new_v4().to_string()),
                title: decode_epg_text(entry.title),
                description: decode_epg_text(entry.description),
                start: entry.start.unwrap_or_else(|| "Unknown".into()),
                end: entry.end.unwrap_or_else(|| "Unknown".into()),
                stream_id: entry.stream_id.unwrap_or_else(|| stream_id.to_string()),
            })
            .collect())
    }

    async fn xmltv_snapshot(&self) -> Result<XmltvSnapshot, XtreamError> {
        let xml = self
            .client
            .get(format!("{}/xmltv.php", self.base_url))
            .query(&[("username", self.username.as_str()), ("password", self.password.as_str())])
            .send()
            .await?
            .error_for_status()?
            .text()
            .await?;

        parse_xmltv_snapshot(&xml).ok_or(XtreamError::InvalidPayload)
    }

    async fn vod_catalog(&self) -> Result<Vec<VodItem>, XtreamError> {
        let categories = self.category_map("get_vod_categories").await.unwrap_or_default();
        let streams = self
            .fetch_player_api::<Vec<RawVodStream>>(Some("get_vod_streams"))
            .await?;

        Ok(streams
            .into_iter()
            .map(|stream| {
                let stream_id = stream.stream_id.unwrap_or_else(|| "0".into());
                let extension = stream
                    .container_extension
                    .clone()
                    .unwrap_or_else(|| "mp4".into());

                VodItem {
                    id: stream_id.clone(),
                    title: stream.name.unwrap_or_else(|| "Untitled VOD".into()),
                    genre: stream
                        .category_id
                        .as_ref()
                        .and_then(|id| categories.get(id).cloned())
                        .unwrap_or_else(|| "Uncategorized".into()),
                    duration_label: stream.duration.unwrap_or_else(|| "Unknown length".into()),
                    quality: extension.to_uppercase(),
                    description: stream
                        .plot
                        .or(stream.description)
                        .unwrap_or_else(|| "No synopsis supplied.".into()),
                    rating: normalize_optional_rating(stream.rating),
                    image_url: stream.stream_icon,
                    stream_url: format!(
                        "{}/movie/{}/{}/{}.{}",
                        self.base_url, self.username, self.password, stream_id, extension
                    ),
                    added: stream.added.and_then(|s| s.parse::<i64>().ok()),
                }
            })
            .collect())
    }

    async fn vod_detail(&self, vod_id: &str) -> Result<VodDetail, XtreamError> {
        let payload = self
            .client
            .get(format!("{}/player_api.php", self.base_url))
            .query(&[
                ("username", self.username.as_str()),
                ("password", self.password.as_str()),
                ("action", "get_vod_info"),
                ("vod_id", vod_id),
            ])
            .send()
            .await?
            .error_for_status()?
            .json::<RawVodInfoPayload>()
            .await?;

        let description = payload
            .info
            .as_ref()
            .and_then(|info| info.plot.clone().or_else(|| info.description.clone()))
            .or_else(|| {
                payload
                    .movie_data
                    .as_ref()
                    .and_then(|entry| entry.plot.clone().or_else(|| entry.description.clone()))
            })
            .unwrap_or_else(|| "No synopsis supplied.".into());

        let duration_label = payload
            .info
            .as_ref()
            .and_then(|info| info.duration.clone());
        let released_at = payload
            .info
            .as_ref()
            .and_then(|info| info.releasedate.clone());
        let image_url = payload
            .info
            .as_ref()
            .and_then(|info| info.cover_big.clone().or_else(|| info.movie_image.clone()));
        let genre = payload
            .info
            .as_ref()
            .and_then(|info| info.genre.clone());
        let rating = payload
            .info
            .as_ref()
            .and_then(|info| info.rating.clone())
            .or_else(|| payload.movie_data.as_ref().and_then(|entry| entry.rating.clone()))
            .and_then(normalize_rating);

        Ok(VodDetail {
            vod_id: vod_id.to_string(),
            description,
            duration_label,
            released_at,
            image_url,
            genre,
            rating,
        })
    }

    async fn series_catalog(&self) -> Result<Vec<SeriesItem>, XtreamError> {
        let categories = self.category_map("get_series_categories").await.unwrap_or_default();
        let series = self
            .fetch_player_api::<Vec<RawSeriesStream>>(Some("get_series"))
            .await?;

        Ok(series
            .into_iter()
            .map(|entry| SeriesItem {
                id: entry.series_id.clone().unwrap_or_else(|| Uuid::new_v4().to_string()),
                title: entry.name.unwrap_or_else(|| "Untitled series".into()),
                genre: entry
                    .category_id
                    .as_ref()
                    .and_then(|id| categories.get(id).cloned())
                    .unwrap_or_else(|| "Uncategorized".into()),
                season_count: entry.seasons.unwrap_or(0),
                episode_count: entry.episodes.unwrap_or(0),
                description: entry.plot.unwrap_or_else(|| "No synopsis supplied.".into()),
                rating: normalize_optional_rating(entry.rating),
                first_episode_title: "Load series info for episodes".into(),
                image_url: entry.cover,
                stream_url: String::new(),
                last_modified: entry.last_modified.and_then(|s| s.parse::<i64>().ok()),
            })
            .collect())
    }

    async fn series_detail(&self, series_id: &str) -> Result<SeriesDetail, XtreamError> {
        let payload = self
            .client
            .get(format!("{}/player_api.php", self.base_url))
            .query(&[
                ("username", self.username.as_str()),
                ("password", self.password.as_str()),
                ("action", "get_series_info"),
                ("series_id", series_id),
            ])
            .send()
            .await?
            .error_for_status()?
            .json::<RawSeriesInfoPayload>()
            .await?;

        let title = payload
            .info
            .as_ref()
            .and_then(|info| info.name.clone())
            .unwrap_or_else(|| "Untitled series".into());
        let rating = payload
            .info
            .as_ref()
            .and_then(|info| info.rating.clone())
            .and_then(normalize_rating);

        let mut seasons = payload
            .seasons
            .into_iter()
            .map(|season| SeriesSeason {
                id: season.id.unwrap_or_else(|| Uuid::new_v4().to_string()),
                name: season.name.unwrap_or_else(|| format!("Season {}", season.season_number.unwrap_or(0))),
                season_number: season.season_number.unwrap_or(0),
                episode_count: season.episode_count.unwrap_or(0),
                air_date: season.air_date,
                overview: season.overview.unwrap_or_default(),
                cover: season.cover_big.or(season.cover),
            })
            .collect::<Vec<_>>();
        seasons.sort_by_key(|season| season.season_number);

        let mut episodes_by_season = HashMap::<String, Vec<SeriesEpisode>>::new();
        for (season_key, episodes) in payload.episodes {
            for episode in episodes {
                let episode_id = episode.id.unwrap_or_else(|| Uuid::new_v4().to_string());
                let season_number = episode.season.or_else(|| season_key.parse::<i64>().ok()).unwrap_or(0);
                let extension = episode.container_extension.unwrap_or_else(|| "mp4".into());
                let stream_url = if let Some(direct_source) = episode.direct_source.clone() {
                    if !direct_source.trim().is_empty() {
                        direct_source
                    } else {
                        format!(
                            "{}/series/{}/{}/{}.{}",
                            self.base_url, self.username, self.password, episode_id, extension
                        )
                    }
                } else {
                    format!(
                        "{}/series/{}/{}/{}.{}",
                        self.base_url, self.username, self.password, episode_id, extension
                    )
                };

                let mapped = SeriesEpisode {
                    id: episode_id,
                    season_number,
                    episode_number: episode.episode_num.unwrap_or(0),
                    title: episode.title.unwrap_or_else(|| "Untitled episode".into()),
                    plot: episode
                        .info
                        .as_ref()
                        .and_then(|info| info.plot.clone())
                        .unwrap_or_default(),
                    duration: episode
                        .info
                        .as_ref()
                        .and_then(|info| info.duration.clone())
                        .unwrap_or_else(|| "Unknown length".into()),
                    released_at: episode.info.as_ref().and_then(|info| info.releasedate.clone()),
                    container_extension: extension,
                    image: episode.info.as_ref().and_then(|info| info.movie_image.clone()),
                    stream_url,
                };

                episodes_by_season
                    .entry(season_number.to_string())
                    .or_default()
                    .push(mapped);
            }
        }

        for episodes in episodes_by_season.values_mut() {
            episodes.sort_by_key(|episode| episode.episode_number);
        }

        let existing_season_numbers = seasons
            .iter()
            .map(|season| season.season_number)
            .collect::<HashSet<_>>();

        let mut inferred_season_numbers = episodes_by_season
            .keys()
            .filter_map(|season_key| season_key.parse::<i64>().ok())
            .filter(|season_number| !existing_season_numbers.contains(season_number))
            .collect::<Vec<_>>();
        inferred_season_numbers.sort_unstable();

        for season_number in inferred_season_numbers {
            let episode_count = episodes_by_season
                .get(&season_number.to_string())
                .map(|episodes| episodes.len() as i64)
                .unwrap_or(0);

            seasons.push(SeriesSeason {
                id: format!("{}-season-{}", series_id, season_number),
                name: format!("Season {}", season_number),
                season_number,
                episode_count,
                air_date: None,
                overview: String::new(),
                cover: None,
            });
        }

        seasons.sort_by_key(|season| season.season_number);

        for season in &seasons {
            episodes_by_season
                .entry(season.season_number.to_string())
                .or_default();
        }

        Ok(SeriesDetail {
            series_id: series_id.to_string(),
            title,
            rating,
            seasons,
            episodes_by_season,
        })
    }
}

#[tauri::command]
pub fn get_bootstrap_state(state: State<'_, AppState>) -> Result<BootstrapState, String> {
    let profiles = sqlite::list_profiles(&state.db_path).map_err(|error| error.to_string())?;
    let favorites = sqlite::list_favorites(&state.db_path).map_err(|error| error.to_string())?;
    let continue_watching =
        sqlite::list_resume_history(&state.db_path).map_err(|error| error.to_string())?;

    Ok(BootstrapState {
        profiles,
        favorites,
        continue_watching,
        capabilities: vec![
            "live-categories".into(),
            "vod-categories".into(),
            "series-browser".into(),
            "short-epg".into(),
            "sqlite-persistence".into(),
        ],
        diagnostics: vec![
            format!("SQLite ready at {}", state.db_path.display()),
            "Xtream commands are available through the Tauri bridge.".into(),
            "Profiles, favorites, and resume history load from Rust before playback wiring.".into(),
        ],
    })
}

#[tauri::command]
pub async fn login_preview(
    server_url: String,
    username: String,
    password: String,
    profile_name: Option<String>,
    state: State<'_, AppState>,
) -> Result<ProviderPreview, String> {
    eprintln!(
        "login_preview: server_url={server_url}, username={username}, has_profile_name={}, password_len={}",
        profile_name.as_ref().map(|value| !value.trim().is_empty()).unwrap_or(false),
        password.len()
    );
    let client = XtreamClient::new(&server_url, &username, &password).map_err(|error| {
        let message = error.to_string();
        eprintln!("login_preview error: {message}");
        message
    })?;
    let auth = client.authenticate().await.map_err(|error| {
        let message = error.to_string();
        eprintln!("login_preview error: {message}");
        message
    })?;

    let mut auth_state = state.auth.lock().expect("auth state poisoned");
    auth_state.last_server_url = server_url.clone();
    auth_state.last_username = username.clone();

    let user_info = auth.user_info.ok_or_else(|| XtreamError::InvalidPayload.to_string())?;
    let display_name = profile_name.filter(|value| !value.trim().is_empty()).unwrap_or(username);

    eprintln!(
        "login_preview success: display_name={display_name}, status={}, max_connections={:?}",
        user_info.status.clone().unwrap_or_else(|| "Unknown".into()),
        user_info.max_connections
    );

    Ok(ProviderPreview {
        display_name,
        server_url,
        account_status: user_info.status.unwrap_or_else(|| "Unknown".into()),
        expires_at: user_info.exp_date,
        max_connections: user_info.max_connections,
        allowed_formats: user_info.allowed_output_formats.unwrap_or_default(),
        message: "Authenticated against player_api.php and resolved basic account metadata.".into(),
        suggested_endpoints: vec![
            "player_api.php?action=get_live_categories".into(),
            "player_api.php?action=get_vod_streams".into(),
            "player_api.php?action=get_series".into(),
            "xmltv.php".into(),
        ],
    })
}

#[tauri::command]
pub async fn save_profile(
    request: SaveProfileRequest,
    state: State<'_, AppState>,
) -> Result<XtreamProfile, String> {
    eprintln!(
        "save_profile: id_present={}, name={}, server_url={}, username={}, password_len={}",
        request.id.is_some(),
        request.name,
        request.server_url,
        request.username,
        request.password.len()
    );
    let client = XtreamClient::new(&request.server_url, &request.username, &request.password)
        .map_err(|error| {
            let message = error.to_string();
            eprintln!("save_profile error: {message}");
            message
        })?;
    let auth = client.authenticate().await.map_err(|error| {
        let message = error.to_string();
        eprintln!("save_profile error: {message}");
        message
    })?;
    let user_info = auth.user_info.ok_or_else(|| {
        let message = XtreamError::InvalidPayload.to_string();
        eprintln!("save_profile error: {message}");
        message
    })?;

    let profile = sqlite::save_profile(
        &state.db_path,
        &request,
        user_info.status.as_deref().unwrap_or("Active"),
        user_info.exp_date.as_deref().unwrap_or("Unknown"),
        user_info.max_connections.unwrap_or(1),
    )
    .map_err(|error| {
        let message = error.to_string();
        eprintln!("save_profile error: {message}");
        message
    })?;

    eprintln!(
        "save_profile success: id={}, name={}, status={}, max_connections={}",
        profile.id, profile.name, profile.status, profile.max_connections
    );

    Ok(profile)
}

#[tauri::command]
pub async fn fetch_live_catalog(
    profile_id: String,
    state: State<'_, AppState>,
) -> Result<Vec<LiveChannel>, String> {
    let profile = sqlite::get_profile_credentials(&state.db_path, &profile_id)
        .map_err(|error| error.to_string())?;
    let client = XtreamClient::new(&profile.server_url, &profile.username, &profile.password)
        .map_err(|error| error.to_string())?;

    client.live_catalog().await.map_err(|error| error.to_string())
}

#[tauri::command]
pub async fn fetch_vod_catalog(
    profile_id: String,
    state: State<'_, AppState>,
) -> Result<Vec<VodItem>, String> {
    let profile = sqlite::get_profile_credentials(&state.db_path, &profile_id)
        .map_err(|error| error.to_string())?;
    let client = XtreamClient::new(&profile.server_url, &profile.username, &profile.password)
        .map_err(|error| error.to_string())?;

    client.vod_catalog().await.map_err(|error| error.to_string())
}

#[tauri::command]
pub async fn fetch_vod_detail(
    profile_id: String,
    vod_id: String,
    state: State<'_, AppState>,
) -> Result<VodDetail, String> {
    let profile = sqlite::get_profile_credentials(&state.db_path, &profile_id)
        .map_err(|error| error.to_string())?;
    let client = XtreamClient::new(&profile.server_url, &profile.username, &profile.password)
        .map_err(|error| error.to_string())?;

    client.vod_detail(&vod_id).await.map_err(|error| error.to_string())
}

#[tauri::command]
pub async fn fetch_series_catalog(
    profile_id: String,
    state: State<'_, AppState>,
) -> Result<Vec<SeriesItem>, String> {
    let profile = sqlite::get_profile_credentials(&state.db_path, &profile_id)
        .map_err(|error| error.to_string())?;
    let client = XtreamClient::new(&profile.server_url, &profile.username, &profile.password)
        .map_err(|error| error.to_string())?;

    client.series_catalog().await.map_err(|error| error.to_string())
}

#[tauri::command]
pub async fn fetch_series_detail(
    profile_id: String,
    series_id: String,
    state: State<'_, AppState>,
) -> Result<SeriesDetail, String> {
    let profile = sqlite::get_profile_credentials(&state.db_path, &profile_id)
        .map_err(|error| error.to_string())?;
    let client = XtreamClient::new(&profile.server_url, &profile.username, &profile.password)
        .map_err(|error| error.to_string())?;

    client.series_detail(&series_id).await.map_err(|error| error.to_string())
}

#[tauri::command]
pub async fn fetch_short_epg(
    profile_id: String,
    stream_id: String,
    limit: Option<usize>,
    state: State<'_, AppState>,
) -> Result<Vec<EpgListing>, String> {
    let profile = sqlite::get_profile_credentials(&state.db_path, &profile_id)
        .map_err(|error| error.to_string())?;
    let client = XtreamClient::new(&profile.server_url, &profile.username, &profile.password)
        .map_err(|error| error.to_string())?;

    client
        .short_epg(&stream_id, limit.unwrap_or(2))
        .await
        .map_err(|error| error.to_string())
}

#[tauri::command]
pub async fn fetch_xmltv_snapshot(
    profile_id: String,
    state: State<'_, AppState>,
) -> Result<XmltvSnapshot, String> {
    let profile = sqlite::get_profile_credentials(&state.db_path, &profile_id)
        .map_err(|error| error.to_string())?;
    let client = XtreamClient::new(&profile.server_url, &profile.username, &profile.password)
        .map_err(|error| error.to_string())?;

    client.xmltv_snapshot().await.map_err(|error| error.to_string())
}

#[tauri::command]
pub fn add_favorite(
    profile_id: String,
    content_id: String,
    title: String,
    kind: String,
    state: State<'_, AppState>,
) -> Result<(), String> {
    sqlite::add_favorite(&state.db_path, &profile_id, &content_id, &title, &kind)
        .map(|_| ())
        .map_err(|error| error.to_string())
}

#[tauri::command]
pub fn remove_favorite(favorite_id: String, state: State<'_, AppState>) -> Result<(), String> {
    sqlite::remove_favorite(&state.db_path, &favorite_id).map_err(|error| error.to_string())
}

#[tauri::command]
pub fn save_resume_checkpoint(
    profile_id: String,
    request: ResumeCheckpointRequest,
    state: State<'_, AppState>,
) -> Result<(), String> {
    sqlite::save_resume_checkpoint(
        &state.db_path,
        &profile_id,
        request.content_id.as_deref(),
        &request.title,
        &request.kind,
        &request.detail,
        request.progress_percent,
    )
    .map(|_| ())
    .map_err(|error| error.to_string())
}

#[derive(Debug, Deserialize)]
struct AuthPayload {
    #[serde(default)]
    user_info: Option<AuthUserInfo>,
}

#[derive(Debug, Deserialize)]
struct AuthUserInfo {
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    auth: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    status: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    exp_date: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_i64")]
    max_connections: Option<i64>,
    #[serde(default, deserialize_with = "deserialize_string_list")]
    allowed_output_formats: Option<Vec<String>>,
}

#[derive(Debug, Deserialize)]
struct RawCategory {
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    category_id: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    category_name: Option<String>,
}

#[derive(Debug, Deserialize)]
struct RawLiveStream {
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    stream_id: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    name: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    category_id: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    epg_channel_id: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_i64")]
    bitrate: Option<i64>,
}

#[derive(Debug, Deserialize)]
struct RawVodStream {
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    stream_id: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    name: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    category_id: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    plot: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    description: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    rating: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    duration: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    container_extension: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    stream_icon: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    added: Option<String>,
}

#[derive(Debug, Deserialize)]
struct RawVodInfoPayload {
    #[serde(default)]
    info: Option<RawVodInfo>,
    #[serde(default)]
    movie_data: Option<RawVodInfoMovieData>,
}

#[derive(Debug, Deserialize)]
struct RawVodInfo {
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    plot: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    description: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    duration: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    releasedate: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    movie_image: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    cover_big: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    genre: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    rating: Option<String>,
}

#[derive(Debug, Deserialize)]
struct RawVodInfoMovieData {
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    plot: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    description: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    rating: Option<String>,
}

#[derive(Debug, Deserialize)]
struct RawSeriesStream {
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    series_id: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    name: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    category_id: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    plot: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    rating: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_i64")]
    seasons: Option<i64>,
    #[serde(default, deserialize_with = "deserialize_optional_i64")]
    episodes: Option<i64>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    cover: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    last_modified: Option<String>,
}

#[derive(Debug, Deserialize)]
struct RawSeriesInfoPayload {
    #[serde(default)]
    info: Option<RawSeriesInfoSummary>,
    #[serde(default)]
    seasons: Vec<RawSeriesSeason>,
    #[serde(default)]
    episodes: HashMap<String, Vec<RawSeriesEpisode>>,
}

#[derive(Debug, Deserialize)]
struct RawSeriesInfoSummary {
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    name: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    rating: Option<String>,
}

#[derive(Debug, Deserialize)]
struct RawSeriesSeason {
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    id: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    name: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_i64")]
    season_number: Option<i64>,
    #[serde(default, deserialize_with = "deserialize_optional_i64")]
    episode_count: Option<i64>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    air_date: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    overview: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    cover: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    cover_big: Option<String>,
}

#[derive(Debug, Deserialize)]
struct RawSeriesEpisode {
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    id: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_i64")]
    episode_num: Option<i64>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    title: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    container_extension: Option<String>,
    #[serde(default)]
    info: Option<RawSeriesEpisodeInfo>,
    #[serde(default, deserialize_with = "deserialize_optional_i64")]
    season: Option<i64>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    direct_source: Option<String>,
}

#[derive(Debug, Deserialize)]
struct RawSeriesEpisodeInfo {
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    plot: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    duration: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    releasedate: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    movie_image: Option<String>,
}

#[derive(Debug, Deserialize)]
struct RawShortEpgPayload {
    #[serde(default)]
    epg_listings: Vec<RawEpgEntry>,
}

#[derive(Debug, Deserialize)]
struct RawEpgEntry {
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    id: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    title: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    description: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    start: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    end: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    stream_id: Option<String>,
}

fn deserialize_optional_string<'de, D>(deserializer: D) -> Result<Option<String>, D::Error>
where
    D: serde::Deserializer<'de>,
{
    let value = Option::<Value>::deserialize(deserializer)?;
    Ok(value.and_then(|inner| match inner {
        Value::Null => None,
        Value::String(text) if text.trim().is_empty() => None,
        Value::String(text) => Some(text),
        Value::Number(number) => Some(number.to_string()),
        Value::Bool(flag) => Some(if flag { "1" } else { "0" }.into()),
        _ => None,
    }))
}

fn deserialize_optional_i64<'de, D>(deserializer: D) -> Result<Option<i64>, D::Error>
where
    D: serde::Deserializer<'de>,
{
    let value = Option::<Value>::deserialize(deserializer)?;
    Ok(match value {
        Some(Value::Number(number)) => number.as_i64(),
        Some(Value::String(text)) => text.parse::<i64>().ok(),
        _ => None,
    })
}

fn normalize_rating(value: String) -> Option<String> {
    let trimmed = value.trim();
    if trimmed.is_empty() {
        return None;
    }

    let parsed = trimmed.parse::<f64>().ok()?;
    if parsed <= 0.0 {
        return None;
    }

    if (parsed.fract()).abs() < f64::EPSILON {
        Some(format!("{:.0}", parsed))
    } else {
        Some(format!("{:.1}", parsed))
    }
}

fn normalize_optional_rating(value: Option<String>) -> Option<String> {
    value.and_then(normalize_rating)
}

fn deserialize_string_list<'de, D>(deserializer: D) -> Result<Option<Vec<String>>, D::Error>
where
    D: serde::Deserializer<'de>,
{
    let value = Option::<Value>::deserialize(deserializer)?;
    Ok(match value {
        Some(Value::Array(items)) => {
            let values = items
                .into_iter()
                .filter_map(|item| match item {
                    Value::String(text) => Some(text),
                    Value::Number(number) => Some(number.to_string()),
                    _ => None,
                })
                .collect::<Vec<_>>();
            Some(values)
        }
        Some(Value::String(text)) => Some(vec![text]),
        _ => None,
    })
}

fn decode_epg_text(value: Option<String>) -> String {
    value
        .and_then(|text| {
            STANDARD
                .decode(text.as_bytes())
                .ok()
                .and_then(|bytes| String::from_utf8(bytes).ok())
                .or(Some(text))
        })
        .unwrap_or_else(|| "Unknown programme".into())
}

fn parse_xmltv_snapshot(xml: &str) -> Option<XmltvSnapshot> {
    let mut reader = Reader::from_str(xml);
    reader.config_mut().trim_text(true);
    let mut channel_count = 0usize;
    let mut programme_count = 0usize;
    let mut generated_at = None;

    loop {
        match reader.read_event() {
            Ok(Event::Start(event)) => match event.name().as_ref() {
                b"tv" => {
                    for attribute in event.attributes().flatten() {
                        if attribute.key.as_ref() == b"date" {
                            generated_at = Some(String::from_utf8_lossy(attribute.value.as_ref()).to_string());
                        }
                    }
                }
                b"channel" => channel_count += 1,
                b"programme" => programme_count += 1,
                _ => {}
            },
            Ok(Event::Eof) => break,
            Err(_) => return None,
            _ => {}
        }
    }

    Some(XmltvSnapshot {
        generated_at,
        channel_count,
        programme_count,
    })
}