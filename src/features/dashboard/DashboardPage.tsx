import { useMemo, useState } from 'react'
import type { AppAction } from '../../stores/appStore'
import { getSelectedProfile } from '../../stores/appStore'
import type { AppState } from '../../types/xtream'

interface DashboardPageProps {
  state: AppState
  dispatch: React.Dispatch<AppAction>
}

interface SearchResult {
  id: string
  title: string
  kind: 'live' | 'vod' | 'series'
  detail: string
  action: () => void
}

export function DashboardPage({ state, dispatch }: DashboardPageProps) {
  const selectedProfile = getSelectedProfile(state)
  const [searchText, setSearchText] = useState('')

  const searchResults = useMemo<SearchResult[]>(() => {
    const query = searchText.trim().toLowerCase()
    if (!query || query.length < 2) return []

    const results: SearchResult[] = []
    const limit = 20

    for (const channel of state.liveChannels) {
      if (results.length >= limit) break
      if (channel.name.toLowerCase().includes(query)) {
        results.push({
          id: `live-${channel.id}`,
          title: channel.name,
          kind: 'live',
          detail: channel.category,
          action: () => {
            dispatch({
              type: 'startPlayback',
              session: {
                title: channel.name,
                streamUrl: channel.streamUrl,
                streamType: 'live',
                preferredEngine: 'Embedded mpv bridge',
                fallbackBehavior: 'External VLC or mpv handoff',
              },
            })
          },
        })
      }
    }

    for (const item of state.vodItems) {
      if (results.length >= limit) break
      if (`${item.title} ${item.genre}`.toLowerCase().includes(query)) {
        results.push({
          id: `vod-${item.id}`,
          title: item.title,
          kind: 'vod',
          detail: item.genre,
          action: () => {
            dispatch({
              type: 'startPlayback',
              session: {
                contentId: item.id,
                title: item.title,
                streamUrl: item.streamUrl,
                streamType: 'vod',
                preferredEngine: 'Embedded mpv bridge',
                fallbackBehavior: 'External VLC or mpv handoff',
              },
            })
          },
        })
      }
    }

    for (const item of state.seriesItems) {
      if (results.length >= limit) break
      if (`${item.title} ${item.genre}`.toLowerCase().includes(query)) {
        results.push({
          id: `series-${item.id}`,
          title: item.title,
          kind: 'series',
          detail: `${item.genre} • ${item.seasonCount} seasons`,
          action: () => dispatch({ type: 'navigateToSeries', seriesId: item.id }),
        })
      }
    }

    return results
  }, [searchText, state.liveChannels, state.vodItems, state.seriesItems, dispatch])
  const quickCards = [
    {
      label: 'Live channels',
      value: state.liveChannels.length,
      detail: 'Guide-ready channels mapped from the active provider.',
      actionLabel: 'Open Live TV',
      action: () => dispatch({ type: 'navigate', view: 'live' }),
    },
    {
      label: 'VOD titles',
      value: state.vodItems.length,
      detail: 'Poster-driven movie browsing with local filtering.',
      actionLabel: 'Browse VOD',
      action: () => dispatch({ type: 'navigate', view: 'vod' }),
    },
    {
      label: 'Series',
      value: state.seriesItems.length,
      detail: 'Season and episode drill-down with resume support.',
      actionLabel: 'Browse series',
      action: () => dispatch({ type: 'navigate', view: 'series' }),
    },
    {
      label: 'Profiles',
      value: state.profiles.length,
      detail: 'Saved providers available for switching and syncing.',
      actionLabel: 'Manage providers',
      action: () => dispatch({ type: 'navigate', view: 'providers' }),
    },
  ]

  const recentContinueWatching = state.continueWatching.slice(0, 4)
  const recentFavorites = state.favorites.slice(0, 5)

  return (
    <div className="view-stack">
      <section className="dashboard-search-bar">
        <input
          id="global-search"
          className="global-search-input"
          value={searchText}
          onChange={(event) => setSearchText(event.target.value)}
          placeholder="Search live channels, movies, series..."
        />
        {searchResults.length > 0 && (
          <div className="search-results-dropdown">
            {searchResults.map((result) => (
              <div key={result.id} className="search-result-row" role="button" tabIndex={0} onClick={result.action} onKeyDown={(e) => { if (e.key === 'Enter') result.action() }}>
                <div className="search-result-info">
                  <strong>{result.title}</strong>
                  <span>{result.detail}</span>
                </div>
                <span className="pill">{result.kind}</span>
              </div>
            ))}
          </div>
        )}
        {searchText.trim().length >= 2 && searchResults.length === 0 && (
          <div className="search-results-dropdown">
            <p className="workspace-copy" style={{ padding: '12px 16px' }}>No results found.</p>
          </div>
        )}
      </section>

      <section className="quick-grid">
        {quickCards.map((item) => (
          <article key={item.label} className="stat-card stat-card--actionable">
            <p className="card-label">{item.label}</p>
            <div className="metric">{item.value}</div>
            <p>{item.detail}</p>
            <button type="button" className="inline-action" onClick={item.action}>
              {item.actionLabel}
            </button>
          </article>
        ))}
      </section>

      <section className="dashboard-grid">
        <article className="panel">
          <p className="card-label">Provider</p>
          <h3>{selectedProfile.name || 'No provider'}</h3>
          <dl className="compact-dl">
            <div><dt>Server</dt><dd>{selectedProfile.serverUrl || '—'}</dd></div>
            <div><dt>Status</dt><dd>{selectedProfile.status || 'Idle'}</dd></div>
            <div><dt>Expiry</dt><dd>{selectedProfile.expiresAt || 'Unknown'}</dd></div>
          </dl>
          <div className="hero-actions" style={{ marginTop: '14px' }}>
            <button type="button" className="primary-action" onClick={() => dispatch({ type: 'navigate', view: 'live' })}>
              Open live guide
            </button>
            <button type="button" className="secondary-action" onClick={() => dispatch({ type: 'navigate', view: 'providers' })}>
              Manage providers
            </button>
          </div>
        </article>

        <article className="panel">
          <div className="panel-sticky-header panel-sticky-header--dense">
            <p className="card-label">Continue watching</p>
          </div>
          <div className="list-stack" style={{ marginTop: '14px' }}>
            {recentContinueWatching.length === 0 ? (
              <div className="list-row list-row--compact">
                <div><h3>No resume history yet</h3></div>
              </div>
            ) : null}
            {recentContinueWatching.map((item) => (
              <div key={item.id} className="list-row list-row--compact">
                <div><h3>{item.title}</h3><p>{item.detail}</p></div>
                <strong>{item.progressPercent}%</strong>
              </div>
            ))}
          </div>
        </article>

        <article className="panel">
          <div className="panel-sticky-header panel-sticky-header--dense">
            <p className="card-label">Favorites</p>
            <span className="pill">{state.favorites.length}</span>
          </div>
          <div className="list-stack" style={{ marginTop: '14px' }}>
            {recentFavorites.length === 0 ? (
              <div className="list-row list-row--compact">
                <div><h3>No favorites yet</h3></div>
              </div>
            ) : null}
            {recentFavorites.map((item) => (
              <div key={item.id} className="list-row list-row--compact">
                <div><h3>{item.title}</h3></div>
                <span className="pill">{item.kind}</span>
              </div>
            ))}
          </div>
        </article>
      </section>
    </div>
  )
}