import { DashboardPage } from '../features/dashboard/DashboardPage'
import { LoginPage } from '../features/auth/LoginPage'
import { LivePage } from '../features/live/LivePage'
import { PlayerPage } from '../features/player/PlayerPage'
import { SeriesPage } from '../features/series/SeriesPage'
import { VodPage } from '../features/vod/VodPage'
import type { AppAction } from '../stores/appStore'
import type { AppState } from '../types/xtream'

interface AppRouterProps {
  state: AppState
  dispatch: React.Dispatch<AppAction>
  refreshBootstrap: () => Promise<void>
  onSelectProfile: (profileId: string) => void
}

export function AppRouter({ state, dispatch, refreshBootstrap, onSelectProfile }: AppRouterProps) {
  switch (state.currentView) {
    case 'providers':
      return (
        <LoginPage
          state={state}
          refreshBootstrap={refreshBootstrap}
          onSelectProfile={onSelectProfile}
        />
      )
    case 'live':
      return (
        <LivePage
          state={state}
          dispatch={dispatch}
          refreshBootstrap={refreshBootstrap}
        />
      )
    case 'vod':
      return (
        <VodPage
          state={state}
          dispatch={dispatch}
          refreshBootstrap={refreshBootstrap}
        />
      )
    case 'series':
      return (
        <SeriesPage
          state={state}
          dispatch={dispatch}
          refreshBootstrap={refreshBootstrap}
        />
      )
    case 'player':
      return (
        <PlayerPage
          state={state}
          dispatch={dispatch}
          refreshBootstrap={refreshBootstrap}
        />
      )
    case 'dashboard':
    default:
      return <DashboardPage state={state} dispatch={dispatch} />
  }
}
