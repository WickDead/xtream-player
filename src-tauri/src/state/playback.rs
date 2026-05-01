use std::process::Child;

#[derive(Debug)]
pub struct PlaybackState {
    pub preferred_engine: String,
    pub fallback_behavior: String,
    pub active_title: Option<String>,
    pub active_url: Option<String>,
    pub transport: String,
    pub paused: bool,
    pub available: bool,
    pub executable_path: Option<String>,
    pub last_error: Option<String>,
    pub internal_pipe: Option<String>,
    pub internal_player: Option<Child>,
}

impl Default for PlaybackState {
    fn default() -> Self {
        Self {
            preferred_engine: "Embedded mpv bridge".into(),
            fallback_behavior: "External VLC or mpv handoff".into(),
            active_title: None,
            active_url: None,
            transport: "uninitialized".into(),
            paused: false,
            available: false,
            executable_path: None,
            last_error: None,
            internal_pipe: None,
            internal_player: None,
        }
    }
}