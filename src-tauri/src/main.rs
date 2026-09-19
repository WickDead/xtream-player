#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod api;
mod models;
mod persistence;
mod state;

use std::{path::PathBuf, sync::Mutex};

use api::xtream::{
    add_favorite, fetch_live_catalog, fetch_series_catalog, fetch_short_epg,
    fetch_series_detail, fetch_vod_catalog, fetch_vod_detail, fetch_xmltv_snapshot, remove_favorite,
    save_profile, save_resume_checkpoint,
};
use api::{
    embedded_proxy::resolve_embedded_stream_url,
    player::{
        cycle_internal_audio_track, get_player_status, open_external_player, seek_internal_playback,
        start_internal_playback, stop_internal_playback, toggle_internal_pause,
    },
    vlc::{start_native_vlc_window_playback, sync_embedded_vlc_bounds},
    xtream::{get_bootstrap_state, login_preview},
};
use persistence::sqlite;
use tauri::Manager;
use state::{auth::AuthState, playback::PlaybackState};

pub struct AppState {
    pub db_path: PathBuf,
    pub auth: Mutex<AuthState>,
    pub playback: Mutex<PlaybackState>,
}

fn main() {
    tauri::Builder::default()
        .setup(|app| {
            let app_data_dir = app
                .path()
                .app_data_dir()
                .map_err(|error| -> Box<dyn std::error::Error> { Box::new(error) })?;
            std::fs::create_dir_all(&app_data_dir)?;
            let db_path = app_data_dir.join(sqlite::database_name());
            sqlite::ensure_database(&db_path)?;

            app.manage(AppState {
                db_path,
                auth: Mutex::new(AuthState::default()),
                playback: Mutex::new(PlaybackState::default()),
            });

            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            get_bootstrap_state,
            login_preview,
            save_profile,
            fetch_live_catalog,
            fetch_vod_catalog,
            fetch_vod_detail,
            fetch_series_catalog,
            fetch_series_detail,
            fetch_short_epg,
            fetch_xmltv_snapshot,
            add_favorite,
            remove_favorite,
            save_resume_checkpoint,
            resolve_embedded_stream_url,
            get_player_status,
            start_internal_playback,
            start_native_vlc_window_playback,
            sync_embedded_vlc_bounds,
            cycle_internal_audio_track,
            toggle_internal_pause,
            seek_internal_playback,
            stop_internal_playback,
            open_external_player,
        ])
        .run(tauri::generate_context!())
        .expect("error while running Tauri application");
}