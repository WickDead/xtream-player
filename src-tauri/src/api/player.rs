use std::{
    fs::OpenOptions,
    io::Write,
    path::PathBuf,
    process::{Command, Stdio},
    thread,
    time::Duration,
};

use serde_json::json;
use tauri::State;

use crate::{models::PlayerStatus, AppState};

#[tauri::command]
pub fn get_player_status(state: State<'_, AppState>) -> PlayerStatus {
    let playback = state.playback.lock().expect("playback state poisoned");
    status_from_state(&playback)
}

#[tauri::command]
pub fn start_internal_playback(
    title: String,
    stream_url: String,
    state: State<'_, AppState>,
) -> Result<PlayerStatus, String> {
    let mut playback = state.playback.lock().expect("playback state poisoned");
    let executable = locate_mpv_executable().ok_or_else(|| {
        playback.last_error = Some(
            "mpv.exe was not found. Install mpv or configure it on PATH for internal playback."
                .into(),
        );
        playback.transport = "external-fallback-required".into();
        playback.available = false;
        "mpv.exe was not found. Install mpv or use the external fallback.".to_string()
    })?;

    ensure_internal_player(&mut playback, &executable)?;
    let pipe_name = playback
        .internal_pipe
        .clone()
        .ok_or_else(|| "Internal mpv IPC pipe is unavailable".to_string())?;

    send_ipc_command(&pipe_name, json!({"command": ["loadfile", stream_url, "replace"]}))?;

    playback.active_title = Some(title);
    playback.active_url = Some(stream_url);
    playback.paused = false;
    playback.available = true;
    playback.transport = "managed-mpv-ipc".into();
    playback.executable_path = Some(executable.to_string_lossy().to_string());
    playback.last_error = None;

    Ok(status_from_state(&playback))
}

#[tauri::command]
pub fn toggle_internal_pause(state: State<'_, AppState>) -> Result<PlayerStatus, String> {
    let mut playback = state.playback.lock().expect("playback state poisoned");
    let pipe_name = playback
        .internal_pipe
        .clone()
        .ok_or_else(|| "Internal mpv engine is not running".to_string())?;

    send_ipc_command(&pipe_name, json!({"command": ["cycle", "pause"]}))?;
    playback.paused = !playback.paused;
    Ok(status_from_state(&playback))
}

#[tauri::command]
pub fn seek_internal_playback(seconds: i64, state: State<'_, AppState>) -> Result<PlayerStatus, String> {
    let playback = &mut *state.playback.lock().expect("playback state poisoned");
    let pipe_name = playback
        .internal_pipe
        .clone()
        .ok_or_else(|| "Internal mpv engine is not running".to_string())?;

    send_ipc_command(&pipe_name, json!({"command": ["seek", seconds, "absolute"]}))?;
    Ok(status_from_state(playback))
}

#[tauri::command]
pub fn stop_internal_playback(state: State<'_, AppState>) -> Result<PlayerStatus, String> {
    let mut playback = state.playback.lock().expect("playback state poisoned");
    if let Some(pipe_name) = playback.internal_pipe.clone() {
        let _ = send_ipc_command(&pipe_name, json!({"command": ["stop"]}));
    }

    playback.active_title = None;
    playback.active_url = None;
    playback.paused = false;
    playback.transport = "managed-mpv-idle".into();
    Ok(status_from_state(&playback))
}

#[tauri::command]
pub fn open_external_player(stream_url: String, state: State<'_, AppState>) -> Result<PlayerStatus, String> {
    let mut playback = state.playback.lock().expect("playback state poisoned");
    let executable = locate_external_player().ok_or_else(|| {
        playback.last_error = Some(
            "No external player was found. Install mpv or VLC to use fallback playback."
                .into(),
        );
        "No external player was found. Install mpv or VLC to use fallback playback.".to_string()
    })?;

    Command::new(&executable)
        .arg(&stream_url)
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .map_err(|error| error.to_string())?;

    playback.active_title = Some(stream_url.clone());
    playback.active_url = Some(stream_url);
    playback.transport = "external-process".into();
    playback.available = true;
    playback.executable_path = Some(executable.to_string_lossy().to_string());
    playback.last_error = None;

    Ok(status_from_state(&playback))
}

fn ensure_internal_player(playback: &mut crate::state::playback::PlaybackState, executable: &PathBuf) -> Result<(), String> {
    let mut restart = false;
    if let Some(child) = playback.internal_player.as_mut() {
        if child.try_wait().map_err(|error| error.to_string())?.is_some() {
            restart = true;
        }
    } else {
        restart = true;
    }

    if restart {
        let pipe_name = format!(r"\\.\pipe\xtream-player-mpv-{}", std::process::id());
        let child = Command::new(executable)
            .args([
                "--idle=yes",
                "--force-window=yes",
                "--keep-open=yes",
                "--no-terminal",
                "--really-quiet",
            ])
            .arg(format!("--input-ipc-server={pipe_name}"))
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .map_err(|error| error.to_string())?;

        playback.internal_pipe = Some(pipe_name);
        playback.internal_player = Some(child);
        thread::sleep(Duration::from_millis(500));
    }

    Ok(())
}

fn send_ipc_command(pipe_name: &str, payload: serde_json::Value) -> Result<(), String> {
    let serialized = format!("{}\n", payload);
    let mut last_error = None;

    for _ in 0..20 {
        match OpenOptions::new().write(true).open(pipe_name) {
            Ok(mut pipe) => {
                pipe.write_all(serialized.as_bytes())
                    .map_err(|error| error.to_string())?;
                pipe.flush().map_err(|error| error.to_string())?;
                return Ok(());
            }
            Err(error) => {
                last_error = Some(error.to_string());
                thread::sleep(Duration::from_millis(150));
            }
        }
    }

    Err(last_error.unwrap_or_else(|| "Unable to connect to mpv IPC pipe".into()))
}

fn locate_mpv_executable() -> Option<PathBuf> {
    locate_from_path("mpv.exe").or_else(|| locate_from_winget("mpv.exe"))
}

fn locate_external_player() -> Option<PathBuf> {
    locate_mpv_executable()
        .or_else(|| locate_from_path("vlc.exe"))
        .or_else(|| locate_from_winget("vlc.exe"))
}

fn locate_from_path(file_name: &str) -> Option<PathBuf> {
    let path_var = std::env::var_os("PATH")?;
    std::env::split_paths(&path_var)
        .map(|segment| segment.join(file_name))
        .find(|candidate| candidate.exists())
}

fn locate_from_winget(file_name: &str) -> Option<PathBuf> {
    let local_app_data = std::env::var_os("LOCALAPPDATA")?;
    let root = PathBuf::from(local_app_data).join(r"Microsoft\WinGet\Packages");
    let entries = std::fs::read_dir(root).ok()?;
    for entry in entries.flatten() {
        let candidate = entry.path().join(file_name);
        if candidate.exists() {
            return Some(candidate);
        }
    }
    None
}

fn status_from_state(playback: &crate::state::playback::PlaybackState) -> PlayerStatus {
    PlayerStatus {
        preferred_engine: playback.preferred_engine.clone(),
        fallback_behavior: playback.fallback_behavior.clone(),
        active_title: playback.active_title.clone(),
        active_url: playback.active_url.clone(),
        paused: playback.paused,
        transport: playback.transport.clone(),
        available: playback.available,
        executable_path: playback.executable_path.clone(),
        last_error: playback.last_error.clone(),
    }
}