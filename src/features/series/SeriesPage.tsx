import { useEffect, useMemo, useRef, useState, useCallback } from 'react'
import { LoadingSkeleton } from '../../components/LoadingSkeleton'
import { useHiddenCategories } from '../../hooks/useHiddenCategories'
import { addFavorite, loadSeriesCatalog, loadSeriesDetail, removeFavorite } from '../../lib/backend'
import type { AppAction } from '../../stores/appStore'
import type { AppState, SeriesDetail, SeriesEpisode, SeriesItem } from '../../types/xtream'

const SERIES_BATCH_SIZE = 200

interface SeriesPageProps {
  state: AppState
  dispatch: React.Dispatch<AppAction>
  refreshBootstrap: () => Promise<void>
}

function startSeriesPlayback(
  dispatch: React.Dispatch<AppAction>,
  item: SeriesItem,
  episode: SeriesEpisode,
  allEpisodes?: SeriesEpisode[],
) {
  const playlist = allEpisodes?.map((ep) => ({
    title: `${item.title} • ${ep.title}`,
    streamUrl: ep.streamUrl,
    episodeNumber: ep.episodeNumber,
    seasonNumber: ep.seasonNumber,
  }))
  const currentIndex = allEpisodes?.findIndex((ep) => ep.id === episode.id) ?? 0

  dispatch({
    type: 'startPlayback',
    session: {
      contentId: item.id,
      title: `${item.title} • ${episode.title}`,
      streamUrl: episode.streamUrl,
      streamType: 'series',
      preferredEngine: 'Embedded mpv bridge',
      fallbackBehavior: 'External VLC or mpv handoff',
      episodePlaylist: playlist,
      currentEpisodeIndex: currentIndex >= 0 ? currentIndex : 0,
    },
  })
}

function getDisplayCounts(item: SeriesItem, detail?: SeriesDetail | null) {
  if (!detail) {
    return {
      seasonCount: item.seasonCount,
      episodeCount: item.episodeCount,
    }
  }

  return {
    seasonCount: detail.seasons.length,
    episodeCount: Object.values(detail.episodesBySeason).reduce(
      (total, episodes) => total + episodes.length,
      0,
    ),
  }
}

function formatRatingLabel(rating?: string | null) {
  if (!rating) {
    return null
  }

  return `${rating}/10`
}

export function SeriesPage({ state, dispatch, refreshBootstrap }: SeriesPageProps) {
  const [, setStatusMessage] = useState('')
  const [statusTone, setStatusTone] = useState<'idle' | 'loading' | 'success' | 'error'>('idle')
  const [selectedCategory, setSelectedCategory] = useState<string>(state.seriesBrowseState.category)
  const [filterText, setFilterText] = useState('')
  const [selectedSeriesId, setSelectedSeriesId] = useState<string>(state.seriesBrowseState.seriesId)
  const [selectedSeason, setSelectedSeason] = useState<number | null>(state.seriesBrowseState.seasonNumber)
  const [seriesDetailById, setSeriesDetailById] = useState<Record<string, SeriesDetail>>({})
  const [detailLoadingId, setDetailLoadingId] = useState<string | null>(null)
  const [visibleLimit, setVisibleLimit] = useState(SERIES_BATCH_SIZE)
  const [showHidden, setShowHidden] = useState(false)
  const [sortOrder, setSortOrder] = useState<'lastModified' | 'title'>('lastModified')
  const autoLoadedProfileRef = useRef<string | null>(null)
  const activeSeriesRef = useRef<HTMLButtonElement | null>(null)
  const activeSeasonRef = useRef<HTMLButtonElement | null>(null)
  const { toggleHidden, isHidden } = useHiddenCategories('series')
  const favoriteIds = useMemo(
    () => new Map(state.favorites.map((favorite) => [favorite.contentId, favorite.id])),
    [state.favorites],
  )
  const favoriteContentIds = useMemo(
    () => new Set(state.favorites.filter((f) => f.kind === 'series').map((f) => f.contentId)),
    [state.favorites],
  )
  const categoryCounts = useMemo(() => {
    const counts = new Map<string, number>()
    for (const item of state.seriesItems) {
      counts.set(item.genre, (counts.get(item.genre) ?? 0) + 1)
    }
    return counts
  }, [state.seriesItems])
  const categories = useMemo(() => {
    const sorted = Array.from(categoryCounts.keys()).sort()
    if (!showHidden) {
      return sorted.filter((cat) => !isHidden(cat))
    }
    return sorted
  }, [categoryCounts, isHidden, showHidden])
  const activeCategory = selectedCategory === '★ Favorites'
    ? '★ Favorites'
    : selectedCategory === '📺 All'
      ? '📺 All'
      : categories.includes(selectedCategory) ? selectedCategory : categories[0] ?? ''
  const categorySeries = activeCategory === '★ Favorites'
    ? state.seriesItems.filter((item) => favoriteContentIds.has(item.id))
    : activeCategory === '📺 All'
      ? state.seriesItems
      : activeCategory
        ? state.seriesItems.filter((item) => item.genre === activeCategory)
        : state.seriesItems
  const normalizedFilter = filterText.trim().toLowerCase()
  const filteredSeries = normalizedFilter
    ? categorySeries.filter((item) => {
        const haystack = `${item.title} ${item.genre}`.toLowerCase()
        return haystack.includes(normalizedFilter)
      })
    : categorySeries
  const visibleSeries = useMemo(() => {
    const sorted = [...filteredSeries]
    if (sortOrder === 'lastModified') {
      sorted.sort((a, b) => (b.lastModified ?? 0) - (a.lastModified ?? 0))
    } else {
      sorted.sort((a, b) => a.title.localeCompare(b.title))
    }
    return sorted
  }, [filteredSeries, sortOrder])
  const displayedSeries = visibleSeries.slice(0, visibleLimit)

  const seriesSentinelRef = useRef<HTMLDivElement | null>(null)
  const seriesObserverRef = useRef<IntersectionObserver | null>(null)
  const seriesSentinelCallback = useCallback((node: HTMLDivElement | null) => {
    if (seriesObserverRef.current) seriesObserverRef.current.disconnect()
    if (!node) return
    seriesObserverRef.current = new IntersectionObserver((entries) => {
      if (entries[0]?.isIntersecting) {
        setVisibleLimit((current) => current + SERIES_BATCH_SIZE)
      }
    }, { rootMargin: '200px' })
    seriesObserverRef.current.observe(node)
    seriesSentinelRef.current = node
  }, [])
  const activeSeries =
    visibleSeries.find((item) => item.id === selectedSeriesId) ?? displayedSeries[0] ?? visibleSeries[0] ?? null
  const activeDetail = activeSeries ? seriesDetailById[activeSeries.id] ?? null : null
  const activeCounts = activeSeries ? getDisplayCounts(activeSeries, activeDetail) : null
  const activeRatingLabel = formatRatingLabel(activeDetail?.rating ?? activeSeries?.rating)
  const activeSeasonNumber =
    activeDetail?.seasons.find((season) => season.seasonNumber === selectedSeason)?.seasonNumber ??
    activeDetail?.seasons[0]?.seasonNumber ??
    null
  const activeEpisodes =
    activeSeasonNumber !== null && activeDetail
      ? activeDetail.episodesBySeason[String(activeSeasonNumber)] ?? []
      : []
  const isInitialCatalogLoading = statusTone === 'loading' && state.seriesItems.length === 0

  async function handleSync() {
    setStatusTone('loading')
    setStatusMessage('Loading series catalog from Xtream...')
    try {
      const items = await loadSeriesCatalog(state.selectedProfileId)
      dispatch({ type: 'setSeriesItems', seriesItems: items })
      setSelectedCategory('')
      setSelectedSeriesId('')
      setSelectedSeason(null)
      setSeriesDetailById({})
      setVisibleLimit(SERIES_BATCH_SIZE)
      setStatusTone('success')
      setStatusMessage(`Loaded ${items.length} series from the backend.`)
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Series sync failed'
      setStatusTone('error')
      setStatusMessage(message)
    }
  }

  useEffect(() => {
    setVisibleLimit(SERIES_BATCH_SIZE)
    setFilterText('')
  }, [activeCategory])

  useEffect(() => {
    const nextBrowseState = {
      category: selectedCategory,
      seriesId: selectedSeriesId,
      seasonNumber: selectedSeason,
    }

    if (
      state.seriesBrowseState.category === nextBrowseState.category &&
      state.seriesBrowseState.seriesId === nextBrowseState.seriesId &&
      state.seriesBrowseState.seasonNumber === nextBrowseState.seasonNumber
    ) {
      return
    }

    dispatch({ type: 'setSeriesBrowseState', browseState: nextBrowseState })
  }, [dispatch, selectedCategory, selectedSeason, selectedSeriesId, state.seriesBrowseState])

  useEffect(() => {
    if (
      !state.selectedProfileId ||
      autoLoadedProfileRef.current === state.selectedProfileId ||
      state.seriesItems.length > 0
    ) {
      return
    }

    autoLoadedProfileRef.current = state.selectedProfileId
    void handleSync()
  }, [state.selectedProfileId, state.seriesItems.length])

  // Deep-link: if navigated here with a pending series ID, select it
  useEffect(() => {
    if (state.pendingSeriesId && state.seriesItems.length > 0) {
      const target = state.seriesItems.find((item) => item.id === state.pendingSeriesId)
      if (target) {
        setSelectedCategory(target.genre || '★ Favorites')
        setSelectedSeriesId(target.id)
        setSelectedSeason(null)
      }
      dispatch({ type: 'navigate', view: 'series' }) // clears pendingSeriesId
    }
  }, [state.pendingSeriesId, state.seriesItems])

  useEffect(() => {
    activeSeriesRef.current?.scrollIntoView({ block: 'nearest', inline: 'nearest' })
  }, [activeSeries?.id])

  useEffect(() => {
    activeSeasonRef.current?.scrollIntoView({ block: 'nearest', inline: 'nearest' })
  }, [activeSeasonNumber])

  useEffect(() => {
    async function fetchDetail() {
      if (!activeSeries || seriesDetailById[activeSeries.id] || !state.selectedProfileId) {
        return
      }

      setDetailLoadingId(activeSeries.id)
  setStatusTone('loading')
      setStatusMessage(`Loading seasons and episodes for ${activeSeries.title}...`)
      try {
        const detail = await loadSeriesDetail(state.selectedProfileId, activeSeries.id)
        setSeriesDetailById((current) => ({
          ...current,
          [activeSeries.id]: detail,
        }))
        setSelectedSeason(detail.seasons[0]?.seasonNumber ?? null)
        setStatusTone('success')
        setStatusMessage(`Loaded ${detail.seasons.length} seasons for ${activeSeries.title}.`)
      } catch (error) {
        const message = error instanceof Error ? error.message : 'Series detail sync failed'
        setStatusTone('error')
        setStatusMessage(message)
      } finally {
        setDetailLoadingId(null)
      }
    }

    void fetchDetail()
  }, [activeSeries, seriesDetailById, state.selectedProfileId])

  async function handleFavoriteToggle(item: SeriesItem) {
    const favoriteId = favoriteIds.get(item.id)
    try {
      if (favoriteId) {
        await removeFavorite(favoriteId)
      } else {
        await addFavorite(state.selectedProfileId, item.id, item.title, 'series')
      }
      await refreshBootstrap()
      setStatusTone('success')
      setStatusMessage(favoriteId ? `Removed ${item.title} from favorites.` : `Added ${item.title} to favorites.`)
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Favorite update failed'
      setStatusTone('error')
      setStatusMessage(message)
    }
  }

  return (
    <div className="view-stack">
      <section className="browse-layout browse-layout-wide">
        <article className="list-card">
          <div className="panel-sticky-header">
            <p className="card-label">Categories</p>
          </div>
          <div className="selection-list" style={{ marginTop: '18px' }}>
            <button
              type="button"
              className={activeCategory === '★ Favorites' ? 'selection-item active' : 'selection-item'}
              onClick={() => {
                setSelectedCategory('★ Favorites')
                setSelectedSeriesId('')
                setSelectedSeason(null)
              }}
            >
              <strong>★ Favorites</strong>
              <span>{favoriteContentIds.size} series</span>
            </button>
            <button
              type="button"
              className={activeCategory === '📺 All' ? 'selection-item active' : 'selection-item'}
              onClick={() => {
                setSelectedCategory('📺 All')
                setSelectedSeriesId('')
                setSelectedSeason(null)
              }}
            >
              <strong>📺 All</strong>
              <span>{state.seriesItems.length} series</span>
            </button>
            {categories.length === 0 ? (
              <div className="list-row">
                <div>
                  <h3>No series categories yet</h3>
                  <p>Sync the series catalog to load provider categories.</p>
                </div>
              </div>
            ) : null}
            {categories.map((category) => (
              <div key={category} className="selection-item-row">
                <button
                  type="button"
                  className={category === activeCategory ? 'selection-item active' : 'selection-item'}
                  onClick={() => {
                    setSelectedCategory(category)
                    setSelectedSeriesId('')
                    setSelectedSeason(null)
                  }}
                >
                  <strong>{isHidden(category) ? `[Hidden] ${category}` : category}</strong>
                  <span>{categoryCounts.get(category) ?? 0} series</span>
                </button>
                <button
                  type="button"
                  className="hide-btn"
                  title={isHidden(category) ? 'Show category' : 'Hide category'}
                  onClick={() => toggleHidden(category)}
                >
                  {isHidden(category) ? '👁' : '✕'}
                </button>
              </div>
            ))}
            <button
              type="button"
              className="inline-action"
              style={{ marginTop: '12px' }}
              onClick={() => setShowHidden(!showHidden)}
            >
              {showHidden ? 'Hide hidden categories' : 'Show hidden categories'}
            </button>
          </div>
        </article>

        <article className="list-card">
          <div className="panel-sticky-header panel-sticky-header--dense">
            <p className="card-label">Series</p>
            <div className="field panel-filter">
              <label htmlFor="series-filter">Filter titles</label>
              <input
                id="series-filter"
                value={filterText}
                onChange={(event) => setFilterText(event.target.value)}
                placeholder="Search this category"
              />
            </div>
            <div className="sort-row">
              <label htmlFor="series-sort">Sort by</label>
              <select
                id="series-sort"
                value={sortOrder}
                onChange={(e) => setSortOrder(e.target.value as 'lastModified' | 'title')}
              >
                <option value="lastModified">Recently Modified</option>
                <option value="title">Title A–Z</option>
              </select>
            </div>
            {visibleSeries.length > 0 ? (
              <p className="workspace-copy">
                Showing {Math.min(displayedSeries.length, visibleSeries.length)} of {visibleSeries.length} series.
              </p>
            ) : null}
          </div>
          <div className="poster-grid" style={{ marginTop: '18px' }}>
            {isInitialCatalogLoading ? <LoadingSkeleton variant="poster" count={6} /> : null}
            {!isInitialCatalogLoading && displayedSeries.map((item) => (
              (() => {
                const displayCounts = getDisplayCounts(item, seriesDetailById[item.id])

                return (
              <button
                key={item.id}
                ref={item.id === activeSeries?.id ? activeSeriesRef : undefined}
                type="button"
                className={item.id === activeSeries?.id ? 'poster-card poster-button active' : 'poster-card poster-button'}
                onClick={() => {
                  setSelectedSeriesId(item.id)
                  setSelectedSeason(null)
                }}
              >
                <div className="poster-art">
                  {item.imageUrl ? (
                    <img src={item.imageUrl} alt={`${item.title} poster`} loading="lazy" />
                  ) : (
                    <div className="poster-fallback">
                      <span>{item.genre}</span>
                      <strong>{item.title}</strong>
                    </div>
                  )}
                  <div className="poster-overlay">
                    <span className="pill">{item.genre}</span>
                    <div className="poster-meta">
                      <h3>{item.title}</h3>
                      <p>{displayCounts.seasonCount} seasons • {displayCounts.episodeCount} episodes</p>
                    </div>
                  </div>
                </div>
              </button>
                )
              })()
            ))}
            {!isInitialCatalogLoading && categories.length > 0 && visibleSeries.length === 0 ? (
              <div className="list-row">
                <div>
                  <h3>No series in this category</h3>
                  <p>Pick another category or resync the series catalog.</p>
                </div>
              </div>
            ) : null}
            {!isInitialCatalogLoading && visibleSeries.length > displayedSeries.length ? (
              <div className="poster-grid-footer" ref={seriesSentinelCallback}>
                <span className="workspace-copy">Loading more…</span>
              </div>
            ) : null}
          </div>
        </article>

        <article className="list-card detail-column">
          <div className="panel-sticky-header panel-sticky-header--dense">
            <p className="card-label">Seasons and Episodes</p>
            <h2>{activeSeries?.title ?? 'Select a series'}</h2>
            {activeSeries ? (
              <div className="path-trail" aria-label="Series path">
                <span className="path-chip">{activeCategory || 'All categories'}</span>
                <span className="path-separator">/</span>
                <span className="path-chip path-chip--active">{activeSeries.title}</span>
                {activeSeasonNumber !== null ? (
                  <>
                    <span className="path-separator">/</span>
                    <span className="path-chip">Season {activeSeasonNumber}</span>
                  </>
                ) : null}
              </div>
            ) : null}
            <p className="workspace-copy">
              Open a series to browse its seasons, then launch a specific episode.
            </p>
          </div>
          {activeSeries ? (
            <div className="stack" style={{ marginTop: '18px' }}>
              <button
                type="button"
                className="inline-action"
                onClick={() => handleFavoriteToggle(activeSeries)}
              >
                {favoriteIds.has(activeSeries.id) ? 'Remove favorite' : 'Add favorite'}
              </button>
              <span className="pill">{activeSeries.genre}</span>
              {activeRatingLabel ? <span className="pill">Rating {activeRatingLabel}</span> : null}
              <span className="pill">{activeCounts?.seasonCount ?? activeSeries.seasonCount} seasons</span>
            </div>
          ) : null}
          {detailLoadingId === activeSeries?.id ? (
            <LoadingSkeleton variant="row" count={4} />
          ) : null}
          {activeDetail ? (
            <div className="series-detail-body">
              <div className="series-season-picker" style={{ marginTop: '18px' }}>
                <div className="pill-row">
                  {activeDetail.seasons.map((season) => (
                    <button
                      key={season.id}
                      ref={season.seasonNumber === activeSeasonNumber ? activeSeasonRef : undefined}
                      type="button"
                      className={season.seasonNumber === activeSeasonNumber ? 'selection-item compact active' : 'selection-item compact'}
                      onClick={() => setSelectedSeason(season.seasonNumber)}
                    >
                      <strong>{season.name}</strong>
                      <span>{season.episodeCount} episodes</span>
                    </button>
                  ))}
                </div>
              </div>
              <div className="list-stack series-episode-list" style={{ marginTop: '18px' }}>
                {activeEpisodes.map((episode) => (
                  <div key={episode.id} className="list-row episode-row">
                    <div className="episode-row__copy">
                      <h3>
                        S{String(episode.seasonNumber).padStart(2, '0')}E{String(episode.episodeNumber).padStart(2, '0')} • {episode.title}
                      </h3>
                      <p>{episode.plot || 'No synopsis supplied.'}</p>
                      <p>
                        {episode.duration} • {episode.releasedAt ?? 'Unknown air date'}
                      </p>
                    </div>
                    <button
                      type="button"
                      className="inline-action episode-row__action"
                      onClick={() => startSeriesPlayback(dispatch, activeSeries, episode, activeEpisodes)}
                    >
                      Play episode
                    </button>
                  </div>
                ))}
                {activeEpisodes.length === 0 ? (
                  <div className="list-row">
                    <div>
                      <h3>No episodes found</h3>
                      <p>This season did not return any episodes from the provider.</p>
                    </div>
                  </div>
                ) : null}
              </div>
            </div>
          ) : activeSeries ? (
            <p className="workspace-copy" style={{ marginTop: '18px' }}>
              Pick a series to load its seasons and episodes.
            </p>
          ) : null}
        </article>
      </section>
    </div>
  )
}