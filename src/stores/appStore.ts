import type {
  AppBootstrap,
  AppState,
  AppView,
  LiveChannel,
  PlaybackSession,
  SeriesBrowseState,
  SeriesItem,
  VodItem,
  XtreamProfile,
} from '../types/xtream'

const defaultSeriesBrowseState: SeriesBrowseState = {
  category: '★ Favorites',
  seriesId: '',
  seasonNumber: null,
}

export const blankProfile: XtreamProfile = {
  id: '',
  name: '',
  serverUrl: '',
  username: '',
  status: 'Idle',
  expiresAt: 'Unknown',
  maxConnections: 0,
}

export type AppAction =
  | { type: 'navigate'; view: AppView }
  | { type: 'navigateToLive'; channelId: string }
  | { type: 'navigateToVod'; vodId: string }
  | { type: 'navigateToSeries'; seriesId: string }
  | { type: 'setSeriesBrowseState'; browseState: SeriesBrowseState }
  | { type: 'selectProfile'; profileId: string }
  | { type: 'startPlayback'; session: PlaybackSession }
  | { type: 'hydrateBootstrap'; bootstrap: AppBootstrap }
  | { type: 'setLiveChannels'; liveChannels: LiveChannel[] }
  | { type: 'setVodItems'; vodItems: VodItem[] }
  | { type: 'setSeriesItems'; seriesItems: SeriesItem[] }
  | { type: 'setDiagnostics'; diagnostics: string[] }

export function createInitialState(selectedProfileId = ''): AppState {
  return {
    currentView: 'dashboard',
    previousView: null,
    selectedProfileId,
    pendingLiveChannelId: null,
    pendingVodId: null,
    pendingSeriesId: null,
    seriesBrowseState: defaultSeriesBrowseState,
    profiles: [],
    favorites: [],
    liveChannels: [],
    vodItems: [],
    seriesItems: [],
    continueWatching: [],
    downloadJobs: [],
    playerSession: null,
    diagnostics: ['No saved Xtream providers yet. Add one from the Providers screen.'],
  }
}

export function appReducer(state: AppState, action: AppAction): AppState {
  switch (action.type) {
    case 'navigate':
      return {
        ...state,
        currentView: action.view,
        pendingLiveChannelId: null,
        pendingVodId: null,
        pendingSeriesId: null,
      }
    case 'navigateToLive':
      return {
        ...state,
        currentView: 'live',
        pendingLiveChannelId: action.channelId,
        pendingVodId: null,
        pendingSeriesId: null,
      }
    case 'navigateToVod':
      return {
        ...state,
        currentView: 'vod',
        pendingLiveChannelId: null,
        pendingVodId: action.vodId,
        pendingSeriesId: null,
      }
    case 'navigateToSeries':
      return {
        ...state,
        currentView: 'series',
        pendingLiveChannelId: null,
        pendingVodId: null,
        pendingSeriesId: action.seriesId,
      }
    case 'setSeriesBrowseState':
      return {
        ...state,
        seriesBrowseState: action.browseState,
      }
    case 'selectProfile':
      return {
        ...state,
        selectedProfileId: action.profileId,
        pendingLiveChannelId: null,
        pendingVodId: null,
        pendingSeriesId: null,
        seriesBrowseState: defaultSeriesBrowseState,
        liveChannels: [],
        vodItems: [],
        seriesItems: [],
      }
    case 'hydrateBootstrap': {
      const profiles = action.bootstrap.profiles
      const selectedProfileId =
        profiles.find((profile) => profile.id === state.selectedProfileId)?.id ??
        profiles[0]?.id ??
        ''

      return {
        ...state,
        profiles,
        favorites: action.bootstrap.favorites,
        continueWatching: action.bootstrap.continueWatching,
        diagnostics: action.bootstrap.diagnostics,
        selectedProfileId,
      }
    }
    case 'setLiveChannels':
      return {
        ...state,
        liveChannels: action.liveChannels,
      }
    case 'setVodItems':
      return {
        ...state,
        vodItems: action.vodItems,
      }
    case 'setSeriesItems':
      return {
        ...state,
        seriesItems: action.seriesItems,
      }
    case 'setDiagnostics':
      return {
        ...state,
        diagnostics: action.diagnostics,
      }
    case 'startPlayback':
      return {
        ...state,
        previousView: state.currentView,
        currentView: 'player',
        playerSession: action.session,
      }
    default:
      return state
  }
}

export function getSelectedProfile(state: AppState): XtreamProfile {
  return (
    state.profiles.find((profile) => profile.id === state.selectedProfileId) ??
    state.profiles[0] ??
    blankProfile
  )
}
