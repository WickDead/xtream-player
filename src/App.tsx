import { useEffect, useReducer, useRef, useState } from 'react'
import { AppRouter } from './app/router'
import {
  appReducer,
  createInitialState,
  getSelectedProfile,
} from './stores/appStore'
import { loadBootstrap, loadLiveCatalog, loadSeriesCatalog, loadVodCatalog } from './lib/backend'
import type { AppView } from './types/xtream'

const navigation: Array<{ view: AppView; label: string }> = [
  { view: 'dashboard', label: 'Overview' },
  { view: 'providers', label: 'Providers' },
  { view: 'live', label: 'Live TV' },
  { view: 'vod', label: 'VOD' },
  { view: 'series', label: 'Series' },
  { view: 'player', label: 'Player' },
]

function App() {
  const [state, dispatch] = useReducer(appReducer, undefined, createInitialState)
  const [bootstrapStatus, setBootstrapStatus] = useState('Loading desktop bridge...')
  const [catalogStatus, setCatalogStatus] = useState('Catalogs idle')
  const [catalogSyncState, setCatalogSyncState] = useState<'idle' | 'loading' | 'success' | 'error'>('idle')
  const syncedProfileRef = useRef<string | null>(null)
  const syncRequestIdRef = useRef(0)
  const selectedProfile = getSelectedProfile(state)

  async function refreshBootstrap() {
    try {
      const bootstrap = await loadBootstrap()
      dispatch({ type: 'hydrateBootstrap', bootstrap })
      setBootstrapStatus('Backend bridge ready')
    } catch (error) {
      const message =
        error instanceof Error ? error.message : 'Failed to load backend bootstrap'
      setBootstrapStatus(message)
    }
  }

  async function syncAllCatalogs(profileId: string, reason: 'startup' | 'manual' | 'profile-change') {
    if (!profileId) {
      setCatalogSyncState('idle')
      setCatalogStatus('Select a provider to load live TV, movies, and series.')
      return
    }

    const requestId = ++syncRequestIdRef.current
    setCatalogSyncState('loading')
    setCatalogStatus(
      reason === 'manual'
        ? 'Refreshing live TV, movies, and series...'
        : 'Loading live TV, movies, and series...',
    )

    const [liveResult, vodResult, seriesResult] = await Promise.allSettled([
      loadLiveCatalog(profileId),
      loadVodCatalog(profileId),
      loadSeriesCatalog(profileId),
    ])

    if (syncRequestIdRef.current !== requestId) {
      return
    }

    const failures: string[] = []

    let liveCount = 0
    let vodCount = 0
    let seriesCount = 0

    if (liveResult.status === 'fulfilled') {
      liveCount = liveResult.value.length
      dispatch({ type: 'setLiveChannels', liveChannels: liveResult.value })
    } else {
      failures.push(`Live TV: ${liveResult.reason instanceof Error ? liveResult.reason.message : 'sync failed'}`)
    }

    if (vodResult.status === 'fulfilled') {
      vodCount = vodResult.value.length
      dispatch({ type: 'setVodItems', vodItems: vodResult.value })
    } else {
      failures.push(`Movies: ${vodResult.reason instanceof Error ? vodResult.reason.message : 'sync failed'}`)
    }

    if (seriesResult.status === 'fulfilled') {
      seriesCount = seriesResult.value.length
      dispatch({ type: 'setSeriesItems', seriesItems: seriesResult.value })
    } else {
      failures.push(`Series: ${seriesResult.reason instanceof Error ? seriesResult.reason.message : 'sync failed'}`)
    }

    syncedProfileRef.current = profileId

    if (failures.length > 0) {
      setCatalogSyncState('error')
      setCatalogStatus(failures.join(' | '))
      return
    }

    setCatalogSyncState('success')
    setCatalogStatus(
      `Loaded ${liveCount} live channels, ${vodCount} movies, and ${seriesCount} series.`,
    )
  }

  async function handleRefreshCatalogs() {
    await syncAllCatalogs(state.selectedProfileId, 'manual')
    await refreshBootstrap()
  }

  useEffect(() => {
    void refreshBootstrap()
  }, [])

  useEffect(() => {
    if (!state.selectedProfileId || syncedProfileRef.current === state.selectedProfileId) {
      return
    }

    void syncAllCatalogs(
      state.selectedProfileId,
      state.liveChannels.length === 0 && state.vodItems.length === 0 && state.seriesItems.length === 0
        ? 'startup'
        : 'profile-change',
    )
  }, [state.selectedProfileId])

  return (
    <div className="application-shell">
      <aside className="sidebar">
        <div>
          <p className="eyebrow">Xtream Player</p>
          <h1 className="sidebar-title">Desktop IPTV foundation</h1>
          <p className="sidebar-copy">
            Windows-first shell with the backend shape for Xtream browsing,
            playback, persistence, and recording.
          </p>
        </div>

        <nav className="nav-list" aria-label="App sections">
          {navigation.map((item) => (
            <button
              key={item.view}
              type="button"
              className={item.view === state.currentView ? 'nav-item active' : 'nav-item'}
              onClick={() => dispatch({ type: 'navigate', view: item.view })}
            >
              {item.label}
            </button>
          ))}
        </nav>

        <div className="sidebar-status">
          <p className="card-label">Active provider</p>
          <strong>{selectedProfile.name || 'No provider selected'}</strong>
          <span>{selectedProfile.serverUrl || 'Add a provider to start syncing Xtream data'}</span>
          <span>{bootstrapStatus}</span>
        </div>
      </aside>

      <section className="workspace">
        <header className="topbar">
          <div>
            <p className="card-label">Current session</p>
            <h2>{selectedProfile.name || 'Provider setup'}</h2>
            <p className="workspace-copy" style={{ marginTop: '8px' }}>{catalogStatus}</p>
          </div>
          <div className="topbar-meta">
            <span className="pill">{selectedProfile.status || 'Idle'}</span>
            <span className="pill">
              {catalogSyncState === 'loading'
                ? 'Catalog sync: loading'
                : catalogSyncState === 'error'
                  ? 'Catalog sync: issue'
                  : catalogSyncState === 'success'
                    ? 'Catalog sync: ready'
                    : 'Catalog sync: idle'}
            </span>
            <button
              type="button"
              className="secondary-action"
              onClick={() => void handleRefreshCatalogs()}
              disabled={!state.selectedProfileId || catalogSyncState === 'loading'}
            >
              {catalogSyncState === 'loading' ? 'Refreshing library...' : 'Refresh library'}
            </button>
            <span className="pill">Preferred engine: mpv bridge</span>
          </div>
        </header>

        <div className="workspace-body">
          <AppRouter
            state={state}
            dispatch={dispatch}
            refreshBootstrap={refreshBootstrap}
          />
        </div>
      </section>
    </div>
  )
}

export default App
