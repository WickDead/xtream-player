use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct XtreamProfile {
    pub id: String,
    pub name: String,
    pub server_url: String,
    pub username: String,
    pub status: String,
    pub expires_at: String,
    pub max_connections: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BootstrapState {
    pub profiles: Vec<XtreamProfile>,
    pub favorites: Vec<FavoriteItem>,
    pub continue_watching: Vec<ContinueWatchingItem>,
    pub capabilities: Vec<String>,
    pub diagnostics: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProviderPreview {
    pub display_name: String,
    pub server_url: String,
    pub account_status: String,
    pub expires_at: Option<String>,
    pub max_connections: Option<i64>,
    pub allowed_formats: Vec<String>,
    pub message: String,
    pub suggested_endpoints: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerStatus {
    pub preferred_engine: String,
    pub fallback_behavior: String,
    pub active_title: Option<String>,
    pub active_url: Option<String>,
    pub paused: bool,
    pub transport: String,
    pub available: bool,
    pub executable_path: Option<String>,
    pub last_error: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SaveProfileRequest {
    pub id: Option<String>,
    pub name: String,
    pub server_url: String,
    pub username: String,
    pub password: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FavoriteItem {
    pub id: String,
    pub content_id: String,
    pub title: String,
    pub kind: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ContinueWatchingItem {
    pub id: String,
    pub content_id: Option<String>,
    pub title: String,
    pub kind: String,
    pub detail: String,
    pub progress_percent: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LiveChannel {
    pub id: String,
    pub name: String,
    pub category: String,
    pub epg_channel_id: Option<String>,
    pub current_program: String,
    pub next_program: String,
    pub bitrate_label: String,
    pub stream_url: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct VodItem {
    pub id: String,
    pub title: String,
    pub genre: String,
    pub duration_label: String,
    pub quality: String,
    pub description: String,
    pub image_url: Option<String>,
    pub stream_url: String,
    pub added: Option<i64>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SeriesItem {
    pub id: String,
    pub title: String,
    pub genre: String,
    pub season_count: i64,
    pub episode_count: i64,
    pub description: String,
    pub first_episode_title: String,
    pub image_url: Option<String>,
    pub stream_url: String,
    pub last_modified: Option<i64>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SeriesSeason {
    pub id: String,
    pub name: String,
    pub season_number: i64,
    pub episode_count: i64,
    pub air_date: Option<String>,
    pub overview: String,
    pub cover: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SeriesEpisode {
    pub id: String,
    pub season_number: i64,
    pub episode_number: i64,
    pub title: String,
    pub plot: String,
    pub duration: String,
    pub released_at: Option<String>,
    pub container_extension: String,
    pub image: Option<String>,
    pub stream_url: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SeriesDetail {
    pub series_id: String,
    pub title: String,
    pub seasons: Vec<SeriesSeason>,
    pub episodes_by_season: std::collections::HashMap<String, Vec<SeriesEpisode>>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EpgListing {
    pub id: String,
    pub title: String,
    pub description: String,
    pub start: String,
    pub end: String,
    pub stream_id: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct XmltvSnapshot {
    pub generated_at: Option<String>,
    pub channel_count: usize,
    pub programme_count: usize,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ResumeCheckpointRequest {
    pub content_id: Option<String>,
    pub title: String,
    pub kind: String,
    pub detail: String,
    pub progress_percent: i64,
}