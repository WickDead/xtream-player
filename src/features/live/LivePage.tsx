import { useEffect, useMemo, useRef, useState } from 'react'
import { LoadingSkeleton } from '../../components/LoadingSkeleton'
import { useHiddenCategories } from '../../hooks/useHiddenCategories'
import {
  addFavorite,
  loadLiveCatalog,
  loadShortEpg,
  removeFavorite,
} from '../../lib/backend'
import type { AppAction } from '../../stores/appStore'
import type { AppState, EpgListing, LiveChannel } from '../../types/xtream'

type CategorySort = 'api' | 'name-asc' | 'name-desc' | 'size-desc'
type ChannelSort = 'api' | 'name-asc' | 'name-desc'

interface LivePageProps {
  state: AppState
  dispatch: React.Dispatch<AppAction>
  refreshBootstrap: () => Promise<void>
}

function startLivePlayback(dispatch: React.Dispatch<AppAction>, channel: LiveChannel) {
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
}

function formatGuideTime(value: string) {
  const parsed = new Date(value)
  if (Number.isNaN(parsed.getTime())) {
    return value
  }

  return parsed.toLocaleTimeString([], {
    hour: 'numeric',
    minute: '2-digit',
  })
}

export function LivePage({ state, dispatch, refreshBootstrap }: LivePageProps) {
  const [, setStatusMessage] = useState('')
  const [statusTone, setStatusTone] = useState<'idle' | 'loading' | 'success' | 'error'>('idle')
  const [epgByStreamId, setEpgByStreamId] = useState<Record<string, EpgListing[]>>({})
  const [selectedCategory, setSelectedCategory] = useState('★ Favorites')
  const [selectedChannelId, setSelectedChannelId] = useState('')
  const [categorySort, setCategorySort] = useState<CategorySort>('api')
  const [channelSort, setChannelSort] = useState<ChannelSort>('api')
  const [channelFilter, setChannelFilter] = useState('')
  const [showHidden, setShowHidden] = useState(false)
  const autoLoadedProfileRef = useRef<string | null>(null)
  const [loadingGuideChannelId, setLoadingGuideChannelId] = useState<string | null>(null)
  const isInitialCatalogLoading = statusTone === 'loading' && state.liveChannels.length === 0
  const { toggleHidden, isHidden } = useHiddenCategories('live')

  const favoriteIds = useMemo(
    () => new Map(state.favorites.map((favorite) => [favorite.contentId, favorite.id])),
    [state.favorites],
  )
  const categoryCounts = useMemo(() => {
    const counts = new Map<string, number>()
    for (const channel of state.liveChannels) {
      counts.set(channel.category, (counts.get(channel.category) ?? 0) + 1)
    }
    return counts
  }, [state.liveChannels])
  const categories = useMemo(() => {
    const items = Array.from(categoryCounts.keys())

    let sorted: string[]
    switch (categorySort) {
      case 'name-asc':
        sorted = [...items].sort((left, right) => left.localeCompare(right))
        break
      case 'name-desc':
        sorted = [...items].sort((left, right) => right.localeCompare(left))
        break
      case 'size-desc':
        sorted = [...items].sort(
          (left, right) => (categoryCounts.get(right) ?? 0) - (categoryCounts.get(left) ?? 0),
        )
        break
      case 'api':
      default:
        sorted = items
    }

    // Filter hidden unless showHidden is true
    if (!showHidden) {
      sorted = sorted.filter((cat) => !isHidden(cat))
    }

    return sorted
  }, [categoryCounts, categorySort, isHidden, showHidden])

  const favoriteChannelIds = useMemo(
    () => new Set(state.favorites.filter((f) => f.kind === 'live').map((f) => f.contentId)),
    [state.favorites],
  )

  const activeCategory = selectedCategory === '★ Favorites'
    ? '★ Favorites'
    : selectedCategory === '📺 All'
      ? '📺 All'
      : categories.includes(selectedCategory)
        ? selectedCategory
        : categories[0] ?? ''
  const baseChannels = activeCategory === '★ Favorites'
    ? state.liveChannels.filter((channel) => favoriteChannelIds.has(channel.id))
    : activeCategory === '📺 All'
      ? state.liveChannels
      : activeCategory
        ? state.liveChannels.filter((channel) => channel.category === activeCategory)
        : state.liveChannels
  const visibleChannels = useMemo(() => {
    const normalizedChannelFilter = channelFilter.trim().toLowerCase()
    const filtered = normalizedChannelFilter
      ? baseChannels.filter((channel) =>
          `${channel.name} ${channel.category}`.toLowerCase().includes(normalizedChannelFilter),
        )
      : baseChannels

    switch (channelSort) {
      case 'name-asc':
        return [...filtered].sort((left, right) => left.name.localeCompare(right.name))
      case 'name-desc':
        return [...filtered].sort((left, right) => right.name.localeCompare(left.name))
      case 'api':
      default:
        return filtered
    }
  }, [baseChannels, channelSort, channelFilter])
  const activeChannel =
    visibleChannels.find((channel) => channel.id === selectedChannelId) ?? visibleChannels[0] ?? null
  const activeGuide = activeChannel ? epgByStreamId[activeChannel.id] ?? [] : []

  async function handleSync() {
    setStatusTone('loading')
    setStatusMessage('Loading live catalog from Xtream...')
    try {
      const channels = await loadLiveCatalog(state.selectedProfileId)
      dispatch({ type: 'setLiveChannels', liveChannels: channels })
      setSelectedCategory('')
      setSelectedChannelId('')
      setStatusTone('success')
      setStatusMessage(`Loaded ${channels.length} live streams from the backend.`)
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Live sync failed'
      setStatusTone('error')
      setStatusMessage(message)
    }
  }

  async function handleRefreshEpg(channel: LiveChannel) {
    setLoadingGuideChannelId(channel.id)
    setStatusTone('loading')
    setStatusMessage(`Loading EPG for ${channel.name}...`)
    try {
      const listings = await loadShortEpg(state.selectedProfileId, channel.id, 8)
      setEpgByStreamId((current) => ({
        ...current,
        [channel.id]: listings,
      }))
      setStatusTone('success')
      setStatusMessage(`Loaded ${listings.length} programme slots for ${channel.name}.`)
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Short EPG sync failed'
      setStatusTone('error')
      setStatusMessage(message)
    } finally {
      setLoadingGuideChannelId(null)
    }
  }

  async function handleFavoriteToggle(channel: LiveChannel) {
    const favoriteId = favoriteIds.get(channel.id)
    try {
      if (favoriteId) {
        await removeFavorite(favoriteId)
      } else {
        await addFavorite(state.selectedProfileId, channel.id, channel.name, 'live')
      }
      await refreshBootstrap()
      setStatusTone('success')
      setStatusMessage(
        favoriteId ? `Removed ${channel.name} from favorites.` : `Added ${channel.name} to favorites.`,
      )
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Favorite update failed'
      setStatusTone('error')
      setStatusMessage(message)
    }
  }

  useEffect(() => {
    if (
      !state.selectedProfileId ||
      autoLoadedProfileRef.current === state.selectedProfileId ||
      state.liveChannels.length > 0
    ) {
      return
    }

    autoLoadedProfileRef.current = state.selectedProfileId
    void handleSync()
  }, [state.selectedProfileId, state.liveChannels.length])

  useEffect(() => {
    if (!visibleChannels.some((channel) => channel.id === selectedChannelId)) {
      setSelectedChannelId(visibleChannels[0]?.id ?? '')
    }
  }, [visibleChannels, selectedChannelId])

  useEffect(() => {
    if (activeChannel && !epgByStreamId[activeChannel.id] && loadingGuideChannelId !== activeChannel.id) {
      void handleRefreshEpg(activeChannel)
    }
  }, [activeChannel, epgByStreamId, loadingGuideChannelId])

  return (
    <div className="view-stack">
      <section className="browse-layout browse-layout-wide">
        <article className="list-card">
          <div className="panel-sticky-header panel-sticky-header--dense">
            <p className="card-label">Categories</p>
            <div className="field panel-filter panel-filter--compact">
              <label htmlFor="live-category-sort">Sort categories</label>
              <select
                id="live-category-sort"
                value={categorySort}
                onChange={(event) => setCategorySort(event.target.value as CategorySort)}
              >
                <option value="api">API order</option>
                <option value="name-asc">Name A-Z</option>
                <option value="name-desc">Name Z-A</option>
                <option value="size-desc">Most channels</option>
              </select>
            </div>
          </div>
          <div className="selection-list" style={{ marginTop: '18px' }}>
            <button
              type="button"
              className={activeCategory === '★ Favorites' ? 'selection-item active' : 'selection-item'}
              onClick={() => {
                setSelectedCategory('★ Favorites')
                setSelectedChannelId('')
              }}
            >
              <strong>★ Favorites</strong>
              <span>{favoriteChannelIds.size} channels</span>
            </button>
            <button
              type="button"
              className={activeCategory === '📺 All' ? 'selection-item active' : 'selection-item'}
              onClick={() => {
                setSelectedCategory('📺 All')
                setSelectedChannelId('')
              }}
            >
              <strong>📺 All</strong>
              <span>{state.liveChannels.length} channels</span>
            </button>
            {categories.length === 0 ? (
              <div className="list-row">
                <div>
                  <h3>No live categories yet</h3>
                  <p>Sync the live catalog to load provider categories.</p>
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
                    setSelectedChannelId('')
                  }}
                >
                  <strong>{isHidden(category) ? `[Hidden] ${category}` : category}</strong>
                  <span>{categoryCounts.get(category) ?? 0} channels</span>
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
            <p className="card-label">Channels</p>
            <h2>{activeCategory || 'All live streams'}</h2>
            <div className="field panel-filter">
              <label htmlFor="live-channel-filter">Search channels</label>
              <input
                id="live-channel-filter"
                value={channelFilter}
                onChange={(event) => setChannelFilter(event.target.value)}
                placeholder="Filter by name..."
              />
            </div>
            <div className="field panel-filter panel-filter--compact">
              <label htmlFor="live-channel-sort">Sort channels</label>
              <select
                id="live-channel-sort"
                value={channelSort}
                onChange={(event) => setChannelSort(event.target.value as ChannelSort)}
              >
                <option value="api">API order</option>
                <option value="name-asc">Name A-Z</option>
                <option value="name-desc">Name Z-A</option>
              </select>
            </div>
            <p className="workspace-copy">
              Select a channel to keep its guide visible in the right-hand panel.
            </p>
          </div>
          <div className="list-stack" style={{ marginTop: '18px' }}>
            {isInitialCatalogLoading ? <LoadingSkeleton variant="row" count={6} /> : null}
            {!isInitialCatalogLoading &&
              visibleChannels.map((channel) => (
                <div
                  key={channel.id}
                  className={channel.id === activeChannel?.id ? 'list-row list-row--selected' : 'list-row'}
                  role="button"
                  tabIndex={0}
                  onClick={() => setSelectedChannelId(channel.id)}
                  onKeyDown={(event) => {
                    if (event.key === 'Enter' || event.key === ' ') {
                      event.preventDefault()
                      setSelectedChannelId(channel.id)
                    }
                  }}
                >
                  <div>
                    <h3>{channel.name}</h3>
                    <p>
                      {channel.category} • {channel.bitrateLabel}
                    </p>
                    <p>
                      <span className="highlight">Now:</span>{' '}
                      {epgByStreamId[channel.id]?.[0]?.title ?? channel.currentProgram}
                    </p>
                    <p>
                      <span className="highlight">Next:</span>{' '}
                      {epgByStreamId[channel.id]?.[1]?.title ?? channel.nextProgram}
                    </p>
                  </div>
                  <div className="stack" style={{ flexDirection: 'column', alignItems: 'stretch', minWidth: '140px' }}>
                    <button
                      type="button"
                      className="inline-action"
                      style={{ width: '100%' }}
                      onClick={(event) => {
                        event.stopPropagation()
                        void handleFavoriteToggle(channel)
                      }}
                    >
                      {favoriteIds.has(channel.id) ? 'Remove favorite' : 'Add favorite'}
                    </button>
                    <button
                      type="button"
                      className="inline-action"
                      style={{ width: '100%' }}
                      onClick={(event) => {
                        event.stopPropagation()
                        startLivePlayback(dispatch, channel)
                      }}
                    >
                      Play channel
                    </button>
                  </div>
                </div>
              ))}
            {!isInitialCatalogLoading && categories.length > 0 && visibleChannels.length === 0 ? (
              <div className="list-row">
                <div>
                  <h3>No channels in this category</h3>
                  <p>Pick another category or resync the provider catalog.</p>
                </div>
              </div>
            ) : null}
          </div>
        </article>

        <article className="list-card detail-column">
          <div className="panel-sticky-header panel-sticky-header--dense">
            <p className="card-label">Guide</p>
            <h2>{activeChannel?.name ?? 'Select a channel'}</h2>
            {activeChannel ? (
              <div className="pill-row">
                <span className="pill">{activeChannel.category}</span>
                <span className="pill">{activeChannel.bitrateLabel}</span>
                <span className="pill">{favoriteIds.has(activeChannel.id) ? 'Favorite' : 'Not favorited'}</span>
              </div>
            ) : null}
            <p className="workspace-copy">
              Keep the selected channel locked while reviewing current and upcoming programme slots.
            </p>
          </div>
          <div className="guide-stack" style={{ marginTop: '18px' }}>
            {activeChannel ? (
              <div className="guide-hero">
                <div className="guide-hero__copy">
                  <p className="card-label">Now playing window</p>
                  <h3>{activeGuide[0]?.title ?? activeChannel.currentProgram}</h3>
                  <p>
                    {activeGuide[0]?.description ||
                      'Guide details load automatically when you select a channel.'}
                  </p>
                </div>
                <div className="stack">
                  <button
                    type="button"
                    className="secondary-action"
                    onClick={() => void handleRefreshEpg(activeChannel)}
                  >
                    Refresh guide
                  </button>
                  <button
                    type="button"
                    className="primary-action"
                    onClick={() => startLivePlayback(dispatch, activeChannel)}
                  >
                    Play selected channel
                  </button>
                </div>
              </div>
            ) : null}

            <div className="list-stack">
              {loadingGuideChannelId === activeChannel?.id ? <LoadingSkeleton variant="row" count={4} /> : null}
              {!activeChannel ? (
                <div className="list-row">
                  <div>
                    <h3>No channel selected</h3>
                    <p>Pick a category and channel to inspect its guide data here.</p>
                  </div>
                </div>
              ) : null}
              {activeChannel && !loadingGuideChannelId && activeGuide.length === 0 ? (
                <div className="list-row">
                  <div>
                    <h3>No short EPG slots yet</h3>
                    <p>The provider did not return guide rows yet for this channel.</p>
                  </div>
                </div>
              ) : null}
              {activeGuide.map((listing) => (
                <div key={listing.id} className="list-row list-row--guide">
                  <div>
                    <h3>{listing.title}</h3>
                    <p>{listing.description || 'No programme synopsis supplied.'}</p>
                  </div>
                  <div className="guide-timebox">
                    <strong>{formatGuideTime(listing.start)}</strong>
                    <span>to {formatGuideTime(listing.end)}</span>
                  </div>
                </div>
              ))}
            </div>
          </div>
        </article>
      </section>
    </div>
  )
}