import { useEffect, useReducer, useRef, useState } from 'react'
import { AppRouter } from './app/router'
import {
  appReducer,
  createInitialState,
  getSelectedProfile,
} from './stores/appStore'
import { loadBootstrap, loadLiveCatalog, loadSeriesCatalog, loadVodCatalog } from './lib/backend'
import type { AppView } from './types/xtream'

const ACTIVE_PROVIDER_STORAGE_KEY = 'xtream-player.active-provider-id'

const navigation: Array<{ view: AppView; label: string }> = [
  { view: 'dashboard', label: 'Overview' },
  { view: 'providers', label: 'Providers' },
  { view: 'live', label: 'Live TV' },
  { view: 'vod', label: 'VOD' },
  { view: 'series', label: 'Series' },
  { view: 'player', label: 'Player' },
]

function formatExpiryLabel(value: string) {
  const trimmed = value.trim()
  if (!trimmed || /unknown|browser fallback/i.test(trimmed)) {
    return 'Expires: Unknown'
  }

  if (/^\d+$/.test(trimmed)) {
    const unixSeconds = Number(trimmed)
    if (Number.isFinite(unixSeconds) && unixSeconds > 0) {
      return `Expires: ${new Date(unixSeconds * 1000).toLocaleDateString([], {
        year: 'numeric',
        month: 'short',
        day: 'numeric',
      })}`
    }
  }

  const parsed = new Date(trimmed)
  if (!Number.isNaN(parsed.getTime())) {
    return `Expires: ${parsed.toLocaleDateString([], {
      year: 'numeric',
      month: 'short',
      day: 'numeric',
    })}`
  }

  return `Expires: ${trimmed}`
}

function formatLastSyncedLabel(value?: number) {
  if (!value) {
    return 'Last synced: Never'
  }

  return `Last synced: ${new Date(value).toLocaleString([], {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
  })}`
}

function App() {
  const [state, dispatch] = useReducer(
    appReducer,
    undefined,
    () => createInitialState(window.localStorage.getItem(ACTIVE_PROVIDER_STORAGE_KEY) ?? ''),
  )
  const [bootstrapStatus, setBootstrapStatus] = useState('Loading desktop bridge...')
  const [catalogStatus, setCatalogStatus] = useState('Catalogs idle')
  const [catalogSyncState, setCatalogSyncState] = useState<'idle' | 'loading' | 'success' | 'error'>('idle')
  const [lastSyncedByProfile, setLastSyncedByProfile] = useState<Record<string, number>>({})
  const syncedProfileRef = useRef<string | null>(null)
  const syncRequestIdRef = useRef(0)
  const selectedProfile = getSelectedProfile(state)

  function selectProfile(profileId: string) {
    if (!profileId || profileId === state.selectedProfileId) {
      return
    }

    ++syncRequestIdRef.current
    syncedProfileRef.current = null
    window.localStorage.setItem(ACTIVE_PROVIDER_STORAGE_KEY, profileId)
    dispatch({ type: 'selectProfile', profileId })
  }

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
    setLastSyncedByProfile((current) => ({
      ...current,
      [profileId]: Date.now(),
    }))
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
    if (state.selectedProfileId) {
      window.localStorage.setItem(ACTIVE_PROVIDER_STORAGE_KEY, state.selectedProfileId)
    } else {
      window.localStorage.removeItem(ACTIVE_PROVIDER_STORAGE_KEY)
    }
  }, [state.selectedProfileId])

  useEffect(() => {
    if (!state.selectedProfileId || syncedProfileRef.current === state.selectedProfileId) {
      return
    }

    void syncAllCatalogs(
      state.selectedProfileId,
      'profile-change',
    )
  }, [state.selectedProfileId])

  const lastSyncedLabel = formatLastSyncedLabel(lastSyncedByProfile[state.selectedProfileId])
  const expiryLabel = formatExpiryLabel(selectedProfile.expiresAt || 'Unknown')

  return (
    <div className="application-shell">
      <aside className="sidebar">
        <div>
          <p className="eyebrow">Xtream Player</p>
          <h1 className="sidebar-title">Desktop IPTV</h1>
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
            <label className="provider-switcher">
              <span>Active provider</span>
              <select
                aria-label="Active provider"
                value={state.selectedProfileId}
                onChange={(event) => selectProfile(event.target.value)}
                disabled={state.profiles.length === 0}
              >
                {state.profiles.length === 0 ? <option value="">No providers saved</option> : null}
                {state.profiles.map((profile) => (
                  <option key={profile.id} value={profile.id}>{profile.name}</option>
                ))}
              </select>
            </label>
            <span className="pill">{expiryLabel}</span>
            <span className="pill">{lastSyncedLabel}</span>
            <button
              type="button"
              className="secondary-action"
              onClick={() => void handleRefreshCatalogs()}
              disabled={!state.selectedProfileId || catalogSyncState === 'loading'}
            >
              {catalogSyncState === 'loading' ? 'Refreshing library...' : 'Refresh library'}
            </button>
          </div>
        </header>

        <div className="workspace-body">
          <AppRouter
            state={state}
            dispatch={dispatch}
            refreshBootstrap={refreshBootstrap}
            onSelectProfile={selectProfile}
          />
        </div>
      </section>
    </div>
  )
}

export default App
