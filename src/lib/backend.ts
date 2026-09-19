import { invoke } from '@tauri-apps/api/core'
import {
  createDemoBootstrap,
  demoLiveChannels,
  demoSeriesItems,
  demoVodItems,
  getDemoSeriesDetail,
} from '../data/demoData'
import type {
  AppBootstrap,
  EpgListing,
  LiveChannel,
  PlayerStatus,
  ProviderPreview,
  ResumeCheckpointRequest,
  SeriesDetail,
  SaveProfileRequest,
  SeriesItem,
  VodDetail,
  XmltvSnapshot,
  VodItem,
  XtreamProfile,
} from '../types/xtream'

type EmbeddedVlcBounds = {
  x: number
  y: number
  width: number
  height: number
}

export type ExternalPlayerPreference = 'vlc' | 'mpv' | 'auto'

function isTauriRuntime() {
  return typeof window !== 'undefined' && '__TAURI_INTERNALS__' in window
}

export async function loadBootstrap(): Promise<AppBootstrap> {
  if (!isTauriRuntime()) {
    return createDemoBootstrap()
  }

  return invoke<AppBootstrap>('get_bootstrap_state')
}

export async function previewProvider(
  request: SaveProfileRequest,
): Promise<ProviderPreview> {
  if (!isTauriRuntime()) {
    return {
      displayName: request.name || request.username,
      serverUrl: request.serverUrl,
      accountStatus: 'Browser fallback',
      expiresAt: null,
      maxConnections: 1,
      allowedFormats: ['ts', 'm3u8', 'mp4'],
      message: 'Preview uses demo mode outside the Tauri shell.',
      suggestedEndpoints: [
        'player_api.php?action=get_live_categories',
        'player_api.php?action=get_vod_streams',
        'player_api.php?action=get_series',
        'xmltv.php',
      ],
    }
  }

  return invoke<ProviderPreview>('login_preview', {
    serverUrl: request.serverUrl,
    username: request.username,
    password: request.password,
    profileName: request.name,
  })
}

export async function saveProviderProfile(
  request: SaveProfileRequest,
): Promise<XtreamProfile> {
  if (!isTauriRuntime()) {
    return {
      id: request.id ?? 'browser-profile',
      name: request.name,
      serverUrl: request.serverUrl,
      username: request.username,
      status: 'Connected',
      expiresAt: 'Browser fallback',
      maxConnections: 1,
    }
  }

  return invoke<XtreamProfile>('save_profile', { request })
}

export async function loadLiveCatalog(profileId: string): Promise<LiveChannel[]> {
  if (!isTauriRuntime()) {
    return demoLiveChannels
  }

  return invoke<LiveChannel[]>('fetch_live_catalog', { profileId })
}

export async function loadVodCatalog(profileId: string): Promise<VodItem[]> {
  if (!isTauriRuntime()) {
    return demoVodItems
  }

  return invoke<VodItem[]>('fetch_vod_catalog', { profileId })
}

export async function loadVodDetail(
  profileId: string,
  vodId: string,
): Promise<VodDetail> {
  if (!isTauriRuntime()) {
    const item = demoVodItems.find((entry) => entry.id === vodId)
    if (!item) {
      throw new Error('VOD detail not found in browser fallback mode.')
    }

    return {
      vodId: item.id,
      description: item.description,
      durationLabel: item.durationLabel,
      releasedAt: null,
      imageUrl: item.imageUrl ?? null,
      genre: item.genre,
    }
  }

  return invoke<VodDetail>('fetch_vod_detail', { profileId, vodId })
}

export async function loadSeriesCatalog(profileId: string): Promise<SeriesItem[]> {
  if (!isTauriRuntime()) {
    return demoSeriesItems
  }

  return invoke<SeriesItem[]>('fetch_series_catalog', { profileId })
}

export async function loadSeriesDetail(
  profileId: string,
  seriesId: string,
): Promise<SeriesDetail> {
  if (!isTauriRuntime()) {
    const detail = getDemoSeriesDetail(seriesId)
    if (!detail) {
      throw new Error('Series detail not found in browser fallback mode.')
    }

    return detail
  }

  return invoke<SeriesDetail>('fetch_series_detail', { profileId, seriesId })
}

export async function loadShortEpg(
  profileId: string,
  streamId: string,
  limit = 2,
): Promise<EpgListing[]> {
  if (!isTauriRuntime()) {
    return []
  }

  return invoke<EpgListing[]>('fetch_short_epg', { profileId, streamId, limit })
}

export async function loadXmltvSnapshot(
  profileId: string,
): Promise<XmltvSnapshot> {
  if (!isTauriRuntime()) {
    return {
      generatedAt: null,
      channelCount: 0,
      programmeCount: 0,
    }
  }

  return invoke<XmltvSnapshot>('fetch_xmltv_snapshot', { profileId })
}

export async function addFavorite(
  profileId: string,
  contentId: string,
  title: string,
  kind: 'live' | 'vod' | 'series',
) {
  if (!isTauriRuntime()) {
    return
  }

  await invoke('add_favorite', {
    profileId,
    contentId,
    title,
    kind,
  })
}

export async function removeFavorite(favoriteId: string) {
  if (!isTauriRuntime()) {
    return
  }

  await invoke('remove_favorite', { favoriteId })
}

export async function saveResumeCheckpoint(
  profileId: string,
  request: ResumeCheckpointRequest,
) {
  if (!isTauriRuntime()) {
    return
  }

  await invoke('save_resume_checkpoint', {
    profileId,
    request,
  })
}

export async function startInternalPlayback(
  title: string,
  streamUrl: string,
): Promise<PlayerStatus> {
  if (!isTauriRuntime()) {
    return {
      preferredEngine: 'Browser fallback',
      fallbackBehavior: 'External handoff unavailable outside Tauri',
      activeTitle: title,
      activeUrl: streamUrl,
      paused: false,
      transport: 'browser-fallback',
      available: false,
      executablePath: null,
      lastError: 'Internal mpv bridge is only available in the Tauri shell.',
    }
  }

  return invoke<PlayerStatus>('start_internal_playback', { title, streamUrl })
}

export async function resolveEmbeddedStreamUrl(streamUrl: string): Promise<string> {
  if (!isTauriRuntime()) {
    return streamUrl
  }

  return invoke<string>('resolve_embedded_stream_url', { streamUrl })
}

export async function startNativeVlcWindowPlayback(
  title: string,
  streamUrl: string,
  bounds: EmbeddedVlcBounds,
): Promise<PlayerStatus> {
  if (!isTauriRuntime()) {
    return {
      preferredEngine: 'Browser fallback',
      fallbackBehavior: 'Embedded VLC playback is unavailable outside Tauri',
      activeTitle: title,
      activeUrl: streamUrl,
      paused: false,
      transport: 'browser-fallback',
      available: false,
      executablePath: null,
      lastError: 'The embedded VLC player is only available in the Tauri shell.',
    }
  }

  return invoke<PlayerStatus>('start_native_vlc_window_playback', { title, streamUrl, bounds })
}

export async function syncEmbeddedVlcBounds(bounds: EmbeddedVlcBounds): Promise<void> {
  if (!isTauriRuntime()) {
    return
  }

  await invoke('sync_embedded_vlc_bounds', { bounds })
}

export async function cycleInternalAudioTrack(): Promise<PlayerStatus> {
  if (!isTauriRuntime()) {
    throw new Error('Internal playback is unavailable outside Tauri.')
  }

  return invoke<PlayerStatus>('cycle_internal_audio_track')
}

export async function toggleInternalPause(): Promise<PlayerStatus> {
  if (!isTauriRuntime()) {
    throw new Error('Internal playback is unavailable outside Tauri.')
  }

  return invoke<PlayerStatus>('toggle_internal_pause')
}

export async function seekInternalPlayback(seconds: number): Promise<PlayerStatus> {
  if (!isTauriRuntime()) {
    throw new Error('Internal playback is unavailable outside Tauri.')
  }

  return invoke<PlayerStatus>('seek_internal_playback', { seconds })
}

export async function stopInternalPlayback(): Promise<PlayerStatus> {
  if (!isTauriRuntime()) {
    throw new Error('Internal playback is unavailable outside Tauri.')
  }

  return invoke<PlayerStatus>('stop_internal_playback')
}

export async function openExternalPlayer(
  streamUrl: string,
  preferredPlayer: ExternalPlayerPreference,
): Promise<PlayerStatus> {
  if (!isTauriRuntime()) {
    throw new Error('External playback is unavailable outside Tauri.')
  }

  return invoke<PlayerStatus>('open_external_player', { streamUrl, preferredPlayer })
}

export async function getPlayerStatus(): Promise<PlayerStatus> {
  if (!isTauriRuntime()) {
    return {
      preferredEngine: 'Browser fallback',
      fallbackBehavior: 'External handoff unavailable outside Tauri',
      activeTitle: null,
      activeUrl: null,
      paused: false,
      transport: 'browser-fallback',
      available: false,
      executablePath: null,
      lastError: null,
    }
  }

  return invoke<PlayerStatus>('get_player_status')
}