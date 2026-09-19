export type AppView = 'dashboard' | 'providers' | 'live' | 'vod' | 'series' | 'player'

export type ConnectionStatus = 'Connected' | 'Idle'
export type ContentKind = 'live' | 'vod' | 'series'
export type DownloadState = 'Queued' | 'Downloading' | 'Completed'

export interface XtreamProfile {
  id: string
  name: string
  serverUrl: string
  username: string
  status: ConnectionStatus
  expiresAt: string
  maxConnections: number
}

export interface SaveProfileRequest {
  id?: string
  name: string
  serverUrl: string
  username: string
  password: string
}

export interface ProviderPreview {
  displayName: string
  serverUrl: string
  accountStatus: string
  expiresAt?: string | null
  maxConnections?: number | null
  allowedFormats: string[]
  message: string
  suggestedEndpoints: string[]
}

export interface LiveChannel {
  id: string
  name: string
  category: string
  categoryOrder?: number | null
  epgChannelId?: string
  currentProgram: string
  nextProgram: string
  bitrateLabel: string
  streamUrl: string
}

export interface VodItem {
  id: string
  title: string
  genre: string
  durationLabel: string
  quality: string
  description: string
  rating?: string | null
  imageUrl?: string | null
  streamUrl: string
  added?: number | null
}

export interface VodDetail {
  vodId: string
  description: string
  durationLabel?: string | null
  releasedAt?: string | null
  imageUrl?: string | null
  genre?: string | null
  rating?: string | null
}

export interface SeriesItem {
  id: string
  title: string
  genre: string
  seasonCount: number
  episodeCount: number
  description: string
  rating?: string | null
  firstEpisodeTitle: string
  imageUrl?: string | null
  streamUrl: string
  lastModified?: number | null
}

export interface SeriesSeason {
  id: string
  name: string
  seasonNumber: number
  episodeCount: number
  airDate?: string | null
  overview: string
  cover?: string | null
}

export interface SeriesEpisode {
  id: string
  seasonNumber: number
  episodeNumber: number
  title: string
  plot: string
  duration: string
  releasedAt?: string | null
  containerExtension: string
  image?: string | null
  streamUrl: string
}

export interface SeriesDetail {
  seriesId: string
  title: string
  rating?: string | null
  seasons: SeriesSeason[]
  episodesBySeason: Record<string, SeriesEpisode[]>
}

export interface ContinueWatchingItem {
  id: string
  title: string
  kind: Exclude<ContentKind, 'live'>
  detail: string
  progressPercent: number
}

export interface FavoriteItem {
  id: string
  contentId: string
  title: string
  kind: ContentKind
}

export interface DownloadJob {
  id: string
  title: string
  state: DownloadState
  detail: string
}

export interface PlaybackSession {
  contentId?: string
  title: string
  streamUrl: string
  streamType: ContentKind
  preferredEngine: string
  fallbackBehavior: string
  episodePlaylist?: EpisodePlaylistEntry[]
  currentEpisodeIndex?: number
}

export interface EpisodePlaylistEntry {
  title: string
  streamUrl: string
  episodeNumber: number
  seasonNumber: number
}

export interface EpgListing {
  id: string
  title: string
  description: string
  start: string
  end: string
  streamId: string
}

export interface XmltvSnapshot {
  generatedAt?: string | null
  channelCount: number
  programmeCount: number
}

export interface PlayerStatus {
  preferredEngine: string
  fallbackBehavior: string
  activeTitle?: string | null
  activeUrl?: string | null
  paused: boolean
  transport: string
  available: boolean
  executablePath?: string | null
  lastError?: string | null
}

export interface ResumeCheckpointRequest {
  contentId?: string
  title: string
  kind: Exclude<ContentKind, 'live'>
  detail: string
  progressPercent: number
}

export interface SeriesBrowseState {
  category: string
  seriesId: string
  seasonNumber: number | null
}

export interface AppState {
  currentView: AppView
  previousView: AppView | null
  selectedProfileId: string
  pendingLiveChannelId: string | null
  pendingVodId: string | null
  pendingSeriesId: string | null
  seriesBrowseState: SeriesBrowseState
  profiles: XtreamProfile[]
  favorites: FavoriteItem[]
  liveChannels: LiveChannel[]
  vodItems: VodItem[]
  seriesItems: SeriesItem[]
  continueWatching: ContinueWatchingItem[]
  downloadJobs: DownloadJob[]
  playerSession: PlaybackSession | null
  diagnostics: string[]
}

export interface AppBootstrap {
  profiles: XtreamProfile[]
  favorites: FavoriteItem[]
  continueWatching: ContinueWatchingItem[]
  diagnostics: string[]
}