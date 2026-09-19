use std::{
    ffi::{c_char, c_int, c_void, CStr, CString},
    fs,
    path::{Path, PathBuf},
    sync::{mpsc, Mutex, OnceLock},
    thread,
    time::Duration,
};

use libloading::{Library, Symbol};
use serde::Deserialize;
use tauri::{AppHandle, State, WebviewWindow};
use windows::{
    core::{w, PCWSTR},
    Win32::{
        Foundation::HWND,
        UI::WindowsAndMessaging::{
            CreateWindowExW, DestroyWindow, MoveWindow, SetWindowPos, ShowWindow, HWND_TOP,
            SW_HIDE, SW_SHOW, WINDOW_EX_STYLE, WS_CHILD, WS_CLIPCHILDREN, WS_CLIPSIBLINGS,
            WS_VISIBLE,
        },
    },
};

use crate::{models::PlayerStatus, AppState};

#[derive(Clone, Copy, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EmbeddedVlcBounds {
    x: i32,
    y: i32,
    width: i32,
    height: i32,
}

impl EmbeddedVlcBounds {
    fn sanitized(self) -> Self {
        Self {
            x: self.x.max(0),
            y: self.y.max(0),
            width: self.width.max(1),
            height: self.height.max(1),
        }
    }
}

enum LibVlcInstance {}
enum LibVlcMedia {}
enum LibVlcMediaPlayer {}

#[repr(C)]
struct LibVlcTrackDescription {
    i_id: c_int,
    psz_name: *mut c_char,
    p_next: *mut LibVlcTrackDescription,
}

type FnLibVlcNew = unsafe extern "C" fn(c_int, *const *const c_char) -> *mut LibVlcInstance;
type FnLibVlcRelease = unsafe extern "C" fn(*mut LibVlcInstance);
type FnLibVlcErrMsg = unsafe extern "C" fn() -> *const c_char;
type FnLibVlcMediaNewLocation = unsafe extern "C" fn(*mut LibVlcInstance, *const c_char) -> *mut LibVlcMedia;
type FnLibVlcMediaRelease = unsafe extern "C" fn(*mut LibVlcMedia);
type FnLibVlcMediaPlayerNew = unsafe extern "C" fn(*mut LibVlcInstance) -> *mut LibVlcMediaPlayer;
type FnLibVlcMediaPlayerRelease = unsafe extern "C" fn(*mut LibVlcMediaPlayer);
type FnLibVlcMediaPlayerSetMedia = unsafe extern "C" fn(*mut LibVlcMediaPlayer, *mut LibVlcMedia);
type FnLibVlcMediaPlayerPlay = unsafe extern "C" fn(*mut LibVlcMediaPlayer) -> c_int;
type FnLibVlcMediaPlayerStop = unsafe extern "C" fn(*mut LibVlcMediaPlayer);
type FnLibVlcMediaPlayerSetHwnd = unsafe extern "C" fn(*mut LibVlcMediaPlayer, *mut c_void);
type FnLibVlcAudioGetTrackDescription = unsafe extern "C" fn(*mut LibVlcMediaPlayer) -> *mut LibVlcTrackDescription;
type FnLibVlcTrackDescriptionListRelease = unsafe extern "C" fn(*mut LibVlcTrackDescription);
type FnLibVlcAudioGetTrack = unsafe extern "C" fn(*mut LibVlcMediaPlayer) -> c_int;
type FnLibVlcAudioSetTrack = unsafe extern "C" fn(*mut LibVlcMediaPlayer, c_int) -> c_int;

struct VlcApi {
    _library: Library,
    libvlc_new: FnLibVlcNew,
    libvlc_release: FnLibVlcRelease,
    libvlc_errmsg: FnLibVlcErrMsg,
    libvlc_media_new_location: FnLibVlcMediaNewLocation,
    libvlc_media_release: FnLibVlcMediaRelease,
    libvlc_media_player_new: FnLibVlcMediaPlayerNew,
    libvlc_media_player_release: FnLibVlcMediaPlayerRelease,
    libvlc_media_player_set_media: FnLibVlcMediaPlayerSetMedia,
    libvlc_media_player_play: FnLibVlcMediaPlayerPlay,
    libvlc_media_player_stop: FnLibVlcMediaPlayerStop,
    libvlc_media_player_set_hwnd: FnLibVlcMediaPlayerSetHwnd,
    libvlc_audio_get_track_description: FnLibVlcAudioGetTrackDescription,
    libvlc_track_description_list_release: FnLibVlcTrackDescriptionListRelease,
    libvlc_audio_get_track: FnLibVlcAudioGetTrack,
    libvlc_audio_set_track: FnLibVlcAudioSetTrack,
}

struct VlcSession {
    instance: *mut LibVlcInstance,
    media: *mut LibVlcMedia,
    player: *mut LibVlcMediaPlayer,
}

unsafe impl Send for VlcSession {}

#[derive(Debug)]
struct VlcHostWindow {
    parent_hwnd: isize,
    host_hwnd: isize,
}

struct VlcRuntime {
    api: Option<VlcApi>,
    install_root: Option<PathBuf>,
    session: Option<VlcSession>,
    host: Option<VlcHostWindow>,
}

impl Default for VlcRuntime {
    fn default() -> Self {
        Self {
            api: None,
            install_root: None,
            session: None,
            host: None,
        }
    }
}

static VLC_RUNTIME: OnceLock<Mutex<VlcRuntime>> = OnceLock::new();

fn vlc_runtime() -> &'static Mutex<VlcRuntime> {
    VLC_RUNTIME.get_or_init(|| Mutex::new(VlcRuntime::default()))
}

#[tauri::command]
pub fn start_native_vlc_window_playback(
    app: AppHandle,
    window: WebviewWindow,
    title: String,
    stream_url: String,
    bounds: EmbeddedVlcBounds,
    state: State<'_, AppState>,
) -> Result<PlayerStatus, String> {
    let parent_hwnd = hwnd_from_window(&window)?;
    let executable_path = start_native_vlc_window(&app, parent_hwnd, &stream_url, bounds)?;

    let mut playback = state.playback.lock().expect("playback state poisoned");
    playback.preferred_engine = "Embedded VLC surface".into();
    playback.fallback_behavior = "Managed mpv bridge or external player handoff".into();
    playback.active_title = Some(title);
    playback.active_url = Some(stream_url);
    playback.transport = "managed-vlc-embedded".into();
    playback.paused = false;
    playback.available = true;
    playback.executable_path = Some(executable_path);
    playback.last_error = None;

    Ok(PlayerStatus {
        preferred_engine: playback.preferred_engine.clone(),
        fallback_behavior: playback.fallback_behavior.clone(),
        active_title: playback.active_title.clone(),
        active_url: playback.active_url.clone(),
        paused: playback.paused,
        transport: playback.transport.clone(),
        available: playback.available,
        executable_path: playback.executable_path.clone(),
        last_error: playback.last_error.clone(),
    })
}

#[tauri::command]
pub fn sync_embedded_vlc_bounds(
    app: AppHandle,
    window: WebviewWindow,
    bounds: EmbeddedVlcBounds,
) -> Result<(), String> {
    let parent_hwnd = hwnd_from_window(&window)?;
    let runtime = vlc_runtime();
    let mut runtime = runtime.lock().expect("vlc runtime poisoned");
    runtime.update_bounds(&app, parent_hwnd, bounds)
}

pub fn start_native_vlc_window(
    app: &AppHandle,
    parent_hwnd: isize,
    stream_url: &str,
    bounds: EmbeddedVlcBounds,
) -> Result<String, String> {
    let runtime = vlc_runtime();
    let mut runtime = runtime.lock().expect("vlc runtime poisoned");
    runtime.start(app, parent_hwnd, stream_url, bounds)?;

    Ok(runtime
        .install_root
        .as_ref()
        .map(|path| path.display().to_string())
        .unwrap_or_else(|| "VLC".to_string()))
}

pub fn cycle_active_vlc_audio_track() -> Result<bool, String> {
    let runtime = vlc_runtime();
    let mut runtime = runtime.lock().expect("vlc runtime poisoned");
    runtime.cycle_audio_track()
}

pub fn stop_active_vlc_playback(app: Option<&AppHandle>) {
    let runtime = vlc_runtime();
    let mut runtime = runtime.lock().expect("vlc runtime poisoned");
    runtime.stop(app);
}

impl VlcRuntime {
    fn start(
        &mut self,
        app: &AppHandle,
        parent_hwnd: isize,
        stream_url: &str,
        bounds: EmbeddedVlcBounds,
    ) -> Result<(), String> {
        self.ensure_loaded()?;
        let host_hwnd = self.ensure_host_window(app, parent_hwnd, bounds)?;
        self.stop_session();

        let api = self.api.as_ref().expect("vlc api initialized");
        let install_root = self.install_root.as_ref().expect("vlc install root initialized");
        let plugin_path = install_root.join("plugins");

        let mut arguments = vec![
            CString::new("--quiet").map_err(|_| "Unable to prepare VLC options".to_string())?,
            CString::new("--no-video-title-show").map_err(|_| "Unable to prepare VLC options".to_string())?,
        ];

        if plugin_path.exists() {
            arguments.push(
                CString::new(format!("--plugin-path={}", plugin_path.display()))
                    .map_err(|_| "Unable to prepare VLC plugin path".to_string())?,
            );
        }

        let argv = arguments.iter().map(|item| item.as_ptr()).collect::<Vec<_>>();
        let instance = unsafe { (api.libvlc_new)(argv.len() as c_int, argv.as_ptr()) };
        if instance.is_null() {
            return Err(self.last_error_message());
        }

        let location = CString::new(stream_url).map_err(|_| "Stream URL contains an unexpected null byte".to_string())?;
        let media = unsafe { (api.libvlc_media_new_location)(instance, location.as_ptr()) };
        if media.is_null() {
            unsafe { (api.libvlc_release)(instance) };
            return Err(self.last_error_message());
        }

        let player = unsafe { (api.libvlc_media_player_new)(instance) };
        if player.is_null() {
            unsafe {
                (api.libvlc_media_release)(media);
                (api.libvlc_release)(instance);
            }
            return Err(self.last_error_message());
        }

        unsafe {
            (api.libvlc_media_player_set_hwnd)(player, host_hwnd as *mut c_void);
            (api.libvlc_media_player_set_media)(player, media);
        }

        let play_result = unsafe { (api.libvlc_media_player_play)(player) };
        if play_result != 0 {
            unsafe {
                (api.libvlc_media_player_release)(player);
                (api.libvlc_media_release)(media);
                (api.libvlc_release)(instance);
            }
            return Err(self.last_error_message());
        }

        self.session = Some(VlcSession {
            instance,
            media,
            player,
        });

        thread::sleep(Duration::from_millis(450));
        let _ = self.prefer_english_audio();

        Ok(())
    }

    fn update_bounds(
        &mut self,
        app: &AppHandle,
        parent_hwnd: isize,
        bounds: EmbeddedVlcBounds,
    ) -> Result<(), String> {
        let host_hwnd = self.ensure_host_window(app, parent_hwnd, bounds)?;

        if let (Some(api), Some(session)) = (self.api.as_ref(), self.session.as_ref()) {
            unsafe {
                (api.libvlc_media_player_set_hwnd)(session.player, host_hwnd as *mut c_void);
            }
        }

        Ok(())
    }

    fn cycle_audio_track(&mut self) -> Result<bool, String> {
        self.ensure_loaded()?;
        let api = self.api.as_ref().expect("vlc api initialized");
        let session = match self.session.as_ref() {
            Some(session) => session,
            None => return Ok(false),
        };

        let descriptions = unsafe { (api.libvlc_audio_get_track_description)(session.player) };
        if descriptions.is_null() {
            return Ok(false);
        }

        let active_track = unsafe { (api.libvlc_audio_get_track)(session.player) };
        let mut track_ids = Vec::new();
        let mut current = descriptions;

        while !current.is_null() {
            let description = unsafe { &*current };
            if description.i_id >= 0 {
                track_ids.push(description.i_id);
            }
            current = description.p_next;
        }

        unsafe { (api.libvlc_track_description_list_release)(descriptions) };

        if track_ids.is_empty() {
            return Ok(false);
        }

        let current_index = track_ids.iter().position(|track_id| *track_id == active_track).unwrap_or(0);
        let next_track = track_ids[(current_index + 1) % track_ids.len()];
        let result = unsafe { (api.libvlc_audio_set_track)(session.player, next_track) };
        if result != 0 {
            return Err(self.last_error_message());
        }

        Ok(true)
    }

    fn prefer_english_audio(&mut self) -> Result<bool, String> {
        self.ensure_loaded()?;
        let api = self.api.as_ref().expect("vlc api initialized");
        let session = match self.session.as_ref() {
            Some(session) => session,
            None => return Ok(false),
        };

        let descriptions = unsafe { (api.libvlc_audio_get_track_description)(session.player) };
        if descriptions.is_null() {
            return Ok(false);
        }

        let mut preferred_track = None;
        let mut current = descriptions;

        while !current.is_null() {
            let description = unsafe { &*current };
            let name = unsafe {
                if description.psz_name.is_null() {
                    None
                } else {
                    Some(CStr::from_ptr(description.psz_name).to_string_lossy().to_lowercase())
                }
            };

            if description.i_id >= 0
                && name
                    .as_deref()
                    .is_some_and(|value| value.contains("english") || value == "eng")
            {
                preferred_track = Some(description.i_id);
                break;
            }

            current = description.p_next;
        }

        unsafe { (api.libvlc_track_description_list_release)(descriptions) };

        let Some(track_id) = preferred_track else {
            return Ok(false);
        };

        let result = unsafe { (api.libvlc_audio_set_track)(session.player, track_id) };
        if result != 0 {
            return Err(self.last_error_message());
        }

        Ok(true)
    }

    fn stop(&mut self, app: Option<&AppHandle>) {
        self.stop_session();

        if let (Some(app), Some(host)) = (app, self.host.as_ref()) {
            let host_hwnd = host.host_hwnd;
            let _ = run_main_thread_result(app, move || {
                unsafe {
                    let _ = ShowWindow(hwnd_from_isize(host_hwnd), SW_HIDE);
                }
                Ok(())
            });
        }
    }

    fn stop_session(&mut self) {
        let Some(session) = self.session.take() else {
            return;
        };

        let Some(api) = self.api.as_ref() else {
            return;
        };

        unsafe {
            (api.libvlc_media_player_stop)(session.player);
            (api.libvlc_media_player_release)(session.player);
            (api.libvlc_media_release)(session.media);
            (api.libvlc_release)(session.instance);
        }
    }

    fn ensure_host_window(
        &mut self,
        app: &AppHandle,
        parent_hwnd: isize,
        bounds: EmbeddedVlcBounds,
    ) -> Result<isize, String> {
        let bounds = bounds.sanitized();

        if let Some(host) = self.host.as_ref() {
            if host.parent_hwnd == parent_hwnd {
                let host_hwnd = host.host_hwnd;
                run_main_thread_result(app, move || update_host_window_bounds(host_hwnd, bounds))?;
                return Ok(host.host_hwnd);
            }

            let host_hwnd = host.host_hwnd;
            run_main_thread_result(app, move || {
                destroy_host_window(host_hwnd);
                Ok(())
            })?;
            self.host = None;
        }

        let host_hwnd = run_main_thread_result(app, move || create_host_window(parent_hwnd, bounds))?;
        self.host = Some(VlcHostWindow {
            parent_hwnd,
            host_hwnd,
        });
        Ok(host_hwnd)
    }

    fn ensure_loaded(&mut self) -> Result<(), String> {
        if self.api.is_some() {
            return Ok(());
        }

        let install_root = locate_vlc_install_root().ok_or_else(|| {
            "VLC was not found. Install desktop VLC to use the embedded VLC player.".to_string()
        })?;

        validate_vlc_architecture(&install_root)?;
        add_directory_to_path(&install_root);

        let dll_path = install_root.join("libvlc.dll");
        let library = unsafe { Library::new(&dll_path) }
            .map_err(|error| format!("Unable to load {}: {error}", dll_path.display()))?;

        let api = unsafe {
            VlcApi {
                libvlc_new: load_symbol(&library, b"libvlc_new\0")?,
                libvlc_release: load_symbol(&library, b"libvlc_release\0")?,
                libvlc_errmsg: load_symbol(&library, b"libvlc_errmsg\0")?,
                libvlc_media_new_location: load_symbol(&library, b"libvlc_media_new_location\0")?,
                libvlc_media_release: load_symbol(&library, b"libvlc_media_release\0")?,
                libvlc_media_player_new: load_symbol(&library, b"libvlc_media_player_new\0")?,
                libvlc_media_player_release: load_symbol(&library, b"libvlc_media_player_release\0")?,
                libvlc_media_player_set_media: load_symbol(&library, b"libvlc_media_player_set_media\0")?,
                libvlc_media_player_play: load_symbol(&library, b"libvlc_media_player_play\0")?,
                libvlc_media_player_stop: load_symbol(&library, b"libvlc_media_player_stop\0")?,
                libvlc_media_player_set_hwnd: load_symbol(&library, b"libvlc_media_player_set_hwnd\0")?,
                libvlc_audio_get_track_description: load_symbol(&library, b"libvlc_audio_get_track_description\0")?,
                libvlc_track_description_list_release: load_symbol(&library, b"libvlc_track_description_list_release\0")?,
                libvlc_audio_get_track: load_symbol(&library, b"libvlc_audio_get_track\0")?,
                libvlc_audio_set_track: load_symbol(&library, b"libvlc_audio_set_track\0")?,
                _library: library,
            }
        };

        self.install_root = Some(install_root);
        self.api = Some(api);
        Ok(())
    }

    fn last_error_message(&self) -> String {
        let Some(api) = self.api.as_ref() else {
            return "VLC initialization failed.".to_string();
        };

        let message_ptr = unsafe { (api.libvlc_errmsg)() };
        if message_ptr.is_null() {
            return "VLC reported an unknown playback error.".to_string();
        }

        unsafe { CStr::from_ptr(message_ptr) }
            .to_string_lossy()
            .trim()
            .to_string()
    }
}

unsafe fn load_symbol<T: Copy>(library: &Library, name: &[u8]) -> Result<T, String> {
    let symbol: Symbol<'_, T> = library
        .get(name)
        .map_err(|error| format!("Unable to load VLC symbol {}: {error}", String::from_utf8_lossy(name)))?;
    Ok(*symbol)
}

fn create_host_window(parent_hwnd: isize, bounds: EmbeddedVlcBounds) -> Result<isize, String> {
    let host_hwnd = unsafe {
        CreateWindowExW(
            WINDOW_EX_STYLE(0),
            w!("STATIC"),
            PCWSTR::null(),
            WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
            bounds.x,
            bounds.y,
            bounds.width,
            bounds.height,
            Some(hwnd_from_isize(parent_hwnd)),
            None,
            None,
            None,
        )
    }
    .map_err(|error| format!("Unable to create embedded VLC surface: {error}"))?;

    unsafe {
        let _ = SetWindowPos(
            host_hwnd,
            Some(HWND_TOP),
            bounds.x,
            bounds.y,
            bounds.width,
            bounds.height,
            Default::default(),
        );
        let _ = ShowWindow(host_hwnd, SW_SHOW);
    }

    Ok(host_hwnd.0 as isize)
}

fn update_host_window_bounds(host_hwnd: isize, bounds: EmbeddedVlcBounds) -> Result<(), String> {
    unsafe {
        MoveWindow(
            hwnd_from_isize(host_hwnd),
            bounds.x,
            bounds.y,
            bounds.width,
            bounds.height,
            true,
        )
        .map_err(|error| format!("Unable to resize embedded VLC surface: {error}"))?;

        SetWindowPos(
            hwnd_from_isize(host_hwnd),
            Some(HWND_TOP),
            bounds.x,
            bounds.y,
            bounds.width,
            bounds.height,
            Default::default(),
        )
        .map_err(|error| format!("Unable to position embedded VLC surface: {error}"))?;

        let _ = ShowWindow(hwnd_from_isize(host_hwnd), SW_SHOW);
    }

    Ok(())
}

fn destroy_host_window(host_hwnd: isize) {
    unsafe {
        let _ = DestroyWindow(hwnd_from_isize(host_hwnd));
    }
}

fn hwnd_from_window(window: &WebviewWindow) -> Result<isize, String> {
    window.hwnd().map(|hwnd| hwnd.0 as isize).map_err(|error| error.to_string())
}

fn hwnd_from_isize(value: isize) -> HWND {
    HWND(value as *mut c_void)
}

fn run_main_thread_result<T: Send + 'static, F>(app: &AppHandle, task: F) -> Result<T, String>
where
    F: FnOnce() -> Result<T, String> + Send + 'static,
{
    let (sender, receiver) = mpsc::channel();
    app.run_on_main_thread(move || {
        let _ = sender.send(task());
    })
    .map_err(|error| error.to_string())?;

    receiver
        .recv()
        .map_err(|error| format!("Unable to receive embedded VLC main-thread result: {error}"))?
}

fn locate_vlc_install_root() -> Option<PathBuf> {
    let candidates = vlc_install_candidates();

    candidates
        .iter()
        .find(|candidate| dll_architecture(&candidate.join("libvlc.dll")) == Some(current_process_architecture()))
        .cloned()
        .or_else(|| candidates.into_iter().next())
}

fn vlc_install_candidates() -> Vec<PathBuf> {
    let mut candidates = Vec::new();

    if let Some(path_candidate) = locate_vlc_from_path() {
        push_unique_path(&mut candidates, path_candidate);
    }

    for candidate in locate_vlc_from_program_files() {
        push_unique_path(&mut candidates, candidate);
    }

    if let Some(winget_candidate) = locate_vlc_from_winget() {
        push_unique_path(&mut candidates, winget_candidate);
    }

    candidates
}

fn push_unique_path(paths: &mut Vec<PathBuf>, candidate: PathBuf) {
    if !paths.iter().any(|existing| existing == &candidate) {
        paths.push(candidate);
    }
}

fn locate_vlc_from_path() -> Option<PathBuf> {
    let path_var = std::env::var_os("PATH")?;
    std::env::split_paths(&path_var)
        .find(|segment| segment.join("vlc.exe").exists() && segment.join("libvlc.dll").exists())
}

fn locate_vlc_from_program_files() -> Vec<PathBuf> {
    [
        PathBuf::from(r"C:\Program Files\VideoLAN\VLC"),
        PathBuf::from(r"C:\Program Files (x86)\VideoLAN\VLC"),
    ]
    .into_iter()
    .find(|candidate| candidate.join("libvlc.dll").exists())
        .into_iter()
        .collect()
}

fn locate_vlc_from_winget() -> Option<PathBuf> {
    let local_app_data = std::env::var_os("LOCALAPPDATA")?;
    let root = PathBuf::from(local_app_data).join(r"Microsoft\WinGet\Packages");
    std::fs::read_dir(root)
        .ok()?
        .flatten()
        .map(|entry| entry.path())
        .find_map(|entry| find_vlc_dir(&entry))
}

fn find_vlc_dir(root: &Path) -> Option<PathBuf> {
    if root.join("libvlc.dll").exists() {
        return Some(root.to_path_buf());
    }

    std::fs::read_dir(root)
        .ok()?
        .flatten()
        .map(|entry| entry.path())
        .find(|path| path.is_dir() && path.join("libvlc.dll").exists())
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum BinaryArchitecture {
    X86,
    X64,
    Arm,
    Arm64,
    Unknown(u16),
}

impl BinaryArchitecture {
    fn label(self) -> &'static str {
        match self {
            Self::X86 => "x86",
            Self::X64 => "x64",
            Self::Arm => "ARM",
            Self::Arm64 => "ARM64",
            Self::Unknown(_) => "unknown",
        }
    }
}

fn current_process_architecture() -> BinaryArchitecture {
    #[cfg(target_arch = "x86")]
    {
        BinaryArchitecture::X86
    }
    #[cfg(target_arch = "x86_64")]
    {
        BinaryArchitecture::X64
    }
    #[cfg(target_arch = "arm")]
    {
        BinaryArchitecture::Arm
    }
    #[cfg(target_arch = "aarch64")]
    {
        BinaryArchitecture::Arm64
    }
}

fn dll_architecture(path: &Path) -> Option<BinaryArchitecture> {
    let bytes = fs::read(path).ok()?;
    if bytes.len() < 0x40 || &bytes[0..2] != b"MZ" {
        return None;
    }

    let pe_offset = u32::from_le_bytes(bytes.get(0x3C..0x40)?.try_into().ok()?) as usize;
    if bytes.len() < pe_offset + 6 || &bytes.get(pe_offset..pe_offset + 4)? != b"PE\0\0" {
        return None;
    }

    let machine = u16::from_le_bytes(bytes.get(pe_offset + 4..pe_offset + 6)?.try_into().ok()?);
    Some(match machine {
        0x014c => BinaryArchitecture::X86,
        0x8664 => BinaryArchitecture::X64,
        0x01c4 => BinaryArchitecture::Arm,
        0xAA64 => BinaryArchitecture::Arm64,
        other => BinaryArchitecture::Unknown(other),
    })
}

fn validate_vlc_architecture(install_root: &Path) -> Result<(), String> {
    let dll_path = install_root.join("libvlc.dll");
    let dll_arch = dll_architecture(&dll_path).ok_or_else(|| {
        format!("Unable to inspect {} to determine whether it matches this app build.", dll_path.display())
    })?;
    let process_arch = current_process_architecture();

    if dll_arch == process_arch {
        return Ok(());
    }

    Err(format!(
        "Installed VLC at {} is {}, but this app is {}. Install a {} VLC build or run a matching app build.",
        install_root.display(),
        dll_arch.label(),
        process_arch.label(),
        process_arch.label(),
    ))
}

fn add_directory_to_path(directory: &Path) {
    let current_path = std::env::var_os("PATH").unwrap_or_default();
    let mut paths = std::env::split_paths(&current_path).collect::<Vec<_>>();
    if paths.iter().any(|existing| existing == directory) {
        return;
    }

    paths.insert(0, directory.to_path_buf());
    if let Ok(joined) = std::env::join_paths(paths) {
        std::env::set_var("PATH", joined);
    }
}