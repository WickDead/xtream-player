import { useEffect, useMemo, useRef, useState, useCallback } from 'react'
import { LoadingSkeleton } from '../../components/LoadingSkeleton'
import { useHiddenCategories } from '../../hooks/useHiddenCategories'
import { addFavorite, loadVodCatalog, removeFavorite } from '../../lib/backend'
import type { AppAction } from '../../stores/appStore'
import type { AppState, VodItem } from '../../types/xtream'

const POSTER_BATCH_SIZE = 200

interface VodPageProps {
  state: AppState
  dispatch: React.Dispatch<AppAction>
  refreshBootstrap: () => Promise<void>
}

function startVodPlayback(dispatch: React.Dispatch<AppAction>, item: VodItem) {
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
}

export function VodPage({ state, dispatch, refreshBootstrap }: VodPageProps) {
  const [, setStatusMessage] = useState('')
  const [statusTone, setStatusTone] = useState<'idle' | 'loading' | 'success' | 'error'>('idle')
  const [selectedCategory, setSelectedCategory] = useState<string>('★ Favorites')
  const [filterText, setFilterText] = useState('')
  const [visibleLimit, setVisibleLimit] = useState(POSTER_BATCH_SIZE)
  const [showHidden, setShowHidden] = useState(false)
  const [sortOrder, setSortOrder] = useState<'added' | 'title'>('added')
  const autoLoadedProfileRef = useRef<string | null>(null)
  const isInitialCatalogLoading = statusTone === 'loading' && state.vodItems.length === 0
  const { toggleHidden, isHidden } = useHiddenCategories('vod')
  const favoriteIds = useMemo(
    () => new Map(state.favorites.map((favorite) => [favorite.contentId, favorite.id])),
    [state.favorites],
  )
  const favoriteContentIds = useMemo(
    () => new Set(state.favorites.filter((f) => f.kind === 'vod').map((f) => f.contentId)),
    [state.favorites],
  )
  const categoryCounts = useMemo(() => {
    const counts = new Map<string, number>()
    for (const item of state.vodItems) {
      counts.set(item.genre, (counts.get(item.genre) ?? 0) + 1)
    }
    return counts
  }, [state.vodItems])
  const categories = useMemo(() => {
    const sorted = Array.from(categoryCounts.keys()).sort()
    if (!showHidden) {
      return sorted.filter((cat) => !isHidden(cat))
    }
    return sorted
  }, [categoryCounts, isHidden, showHidden])
  const activeCategory = selectedCategory === '★ Favorites'
    ? '★ Favorites'
    : selectedCategory === '🎬 All'
      ? '🎬 All'
      : categories.includes(selectedCategory) ? selectedCategory : categories[0] ?? ''
  const categoryItems = activeCategory === '★ Favorites'
    ? state.vodItems.filter((item) => favoriteContentIds.has(item.id))
    : activeCategory === '🎬 All'
      ? state.vodItems
      : activeCategory
        ? state.vodItems.filter((item) => item.genre === activeCategory)
        : state.vodItems
  const normalizedFilter = filterText.trim().toLowerCase()
  const filteredItems = normalizedFilter
    ? categoryItems.filter((item) => {
        const haystack = `${item.title} ${item.description} ${item.genre}`.toLowerCase()
        return haystack.includes(normalizedFilter)
      })
    : categoryItems
  const visibleItems = useMemo(() => {
    const sorted = [...filteredItems]
    if (sortOrder === 'added') {
      sorted.sort((a, b) => (b.added ?? 0) - (a.added ?? 0))
    } else {
      sorted.sort((a, b) => a.title.localeCompare(b.title))
    }
    return sorted
  }, [filteredItems, sortOrder])
  const displayedItems = visibleItems.slice(0, visibleLimit)

  const sentinelRef = useRef<HTMLDivElement | null>(null)
  const observerRef = useRef<IntersectionObserver | null>(null)
  const sentinelCallback = useCallback((node: HTMLDivElement | null) => {
    if (observerRef.current) observerRef.current.disconnect()
    if (!node) return
    observerRef.current = new IntersectionObserver((entries) => {
      if (entries[0]?.isIntersecting) {
        setVisibleLimit((current) => current + POSTER_BATCH_SIZE)
      }
    }, { rootMargin: '200px' })
    observerRef.current.observe(node)
    sentinelRef.current = node
  }, [])

  async function handleSync() {
    setStatusTone('loading')
    setStatusMessage('Loading VOD catalog from Xtream...')
    try {
      const items = await loadVodCatalog(state.selectedProfileId)
      dispatch({ type: 'setVodItems', vodItems: items })
      setSelectedCategory('')
      setVisibleLimit(POSTER_BATCH_SIZE)
      setStatusTone('success')
      setStatusMessage(`Loaded ${items.length} VOD titles from the backend.`)
    } catch (error) {
      const message = error instanceof Error ? error.message : 'VOD sync failed'
      setStatusTone('error')
      setStatusMessage(message)
    }
  }

  useEffect(() => {
    setVisibleLimit(POSTER_BATCH_SIZE)
    setFilterText('')
  }, [activeCategory])

  useEffect(() => {
    if (
      !state.selectedProfileId ||
      autoLoadedProfileRef.current === state.selectedProfileId ||
      state.vodItems.length > 0
    ) {
      return
    }

    autoLoadedProfileRef.current = state.selectedProfileId
    void handleSync()
  }, [state.selectedProfileId, state.vodItems.length])

  async function handleFavoriteToggle(item: VodItem) {
    const favoriteId = favoriteIds.get(item.id)
    try {
      if (favoriteId) {
        await removeFavorite(favoriteId)
      } else {
        await addFavorite(state.selectedProfileId, item.id, item.title, 'vod')
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
      <section className="browse-layout">
        <article className="list-card">
          <div className="panel-sticky-header">
            <p className="card-label">Categories</p>
          </div>
          <div className="selection-list" style={{ marginTop: '18px' }}>
            <button
              type="button"
              className={activeCategory === '★ Favorites' ? 'selection-item active' : 'selection-item'}
              onClick={() => setSelectedCategory('★ Favorites')}
            >
              <strong>★ Favorites</strong>
              <span>{favoriteContentIds.size} titles</span>
            </button>
            <button
              type="button"
              className={activeCategory === '🎬 All' ? 'selection-item active' : 'selection-item'}
              onClick={() => setSelectedCategory('🎬 All')}
            >
              <strong>🎬 All</strong>
              <span>{state.vodItems.length} titles</span>
            </button>
            {categories.length === 0 ? (
              <div className="list-row">
                <div>
                  <h3>No VOD categories yet</h3>
                  <p>Sync the VOD catalog to load provider movie categories.</p>
                </div>
              </div>
            ) : null}
            {categories.map((category) => (
              <div key={category} className="selection-item-row">
                <button
                  type="button"
                  className={category === activeCategory ? 'selection-item active' : 'selection-item'}
                  onClick={() => setSelectedCategory(category)}
                >
                  <strong>{isHidden(category) ? `[Hidden] ${category}` : category}</strong>
                  <span>{categoryCounts.get(category) ?? 0} titles</span>
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

        <article className="list-card detail-column">
          <div className="panel-sticky-header panel-sticky-header--dense">
            <p className="card-label">Titles</p>
            <h2>{activeCategory || 'All movies'}</h2>
            <div className="field panel-filter">
              <label htmlFor="vod-filter">Filter titles</label>
              <input
                id="vod-filter"
                value={filterText}
                onChange={(event) => setFilterText(event.target.value)}
                placeholder="Search this category"
              />
            </div>
            <div className="sort-row">
              <label htmlFor="vod-sort">Sort by</label>
              <select
                id="vod-sort"
                value={sortOrder}
                onChange={(e) => setSortOrder(e.target.value as 'added' | 'title')}
              >
                <option value="added">Recently Added</option>
                <option value="title">Title A–Z</option>
              </select>
            </div>
            {visibleItems.length > 0 ? (
              <p className="workspace-copy">
                Showing {Math.min(displayedItems.length, visibleItems.length)} of {visibleItems.length} titles.
              </p>
            ) : null}
          </div>
          <div className="poster-grid" style={{ marginTop: '18px' }}>
        {isInitialCatalogLoading ? <LoadingSkeleton variant="poster" count={8} /> : null}
        {!isInitialCatalogLoading && displayedItems.map((item) => (
          <article key={item.id} className="poster-card">
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
                <div className="poster-meta">
                  <h3>{item.title}</h3>
                  <p>{item.durationLabel}</p>
                </div>
                <div className="poster-actions">
                  <button
                    type="button"
                    className="poster-action-btn"
                    onClick={() => startVodPlayback(dispatch, item)}
                  >
                    ▶ Play
                  </button>
                  <button
                    type="button"
                    className="poster-action-btn"
                    onClick={() => handleFavoriteToggle(item)}
                  >
                    {favoriteIds.has(item.id) ? '★ Unfav' : '☆ Fav'}
                  </button>
                </div>
              </div>
            </div>
          </article>
        ))}
        {!isInitialCatalogLoading && categories.length > 0 && visibleItems.length === 0 ? (
          <div className="list-row" style={{ marginTop: '18px' }}>
            <div>
              <h3>No titles in this category</h3>
              <p>Pick another category or resync the VOD catalog.</p>
            </div>
          </div>
        ) : null}
        {!isInitialCatalogLoading && visibleItems.length > displayedItems.length ? (
          <div className="poster-grid-footer" ref={sentinelCallback}>
            <span className="workspace-copy">Loading more…</span>
          </div>
        ) : null}
          </div>
        </article>
      </section>
    </div>
  )
}