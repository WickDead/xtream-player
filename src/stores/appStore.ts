import type {
  AppBootstrap,
  AppState,
  AppView,
  LiveChannel,
  PlaybackSession,
  SeriesItem,
  VodItem,
  XtreamProfile,
} from '../types/xtream'

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
  | { type: 'navigateToSeries'; seriesId: string }
  | { type: 'selectProfile'; profileId: string }
  | { type: 'startPlayback'; session: PlaybackSession }
  | { type: 'hydrateBootstrap'; bootstrap: AppBootstrap }
  | { type: 'setLiveChannels'; liveChannels: LiveChannel[] }
  | { type: 'setVodItems'; vodItems: VodItem[] }
  | { type: 'setSeriesItems'; seriesItems: SeriesItem[] }
  | { type: 'setDiagnostics'; diagnostics: string[] }

export function createInitialState(): AppState {
  return {
    currentView: 'dashboard',
    previousView: null,
    selectedProfileId: '',
    pendingSeriesId: null,
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
        pendingSeriesId: null,
      }
    case 'navigateToSeries':
      return {
        ...state,
        currentView: 'series',
        pendingSeriesId: action.seriesId,
      }
    case 'selectProfile':
      return {
        ...state,
        selectedProfileId: action.profileId,
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