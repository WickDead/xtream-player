import { useEffect, useMemo, useRef, useState } from 'react'
import Hls from 'hls.js'
import { StatusBanner, type StatusTone } from '../../components/StatusBanner'
import {
  cycleInternalAudioTrack,
  type ExternalPlayerPreference,
  getPlayerStatus,
  openExternalPlayer,
  resolveEmbeddedStreamUrl,
  syncEmbeddedVlcBounds,
  startInternalPlayback,
  startNativeVlcWindowPlayback,
  stopInternalPlayback,
} from '../../lib/backend'
import type { AppAction } from '../../stores/appStore'
import type { AppState, PlayerStatus } from '../../types/xtream'

interface PlayerPageProps {
  state: AppState
  dispatch: React.Dispatch<AppAction>
  refreshBootstrap: () => Promise<void>
}

type AudioTrackOption = {
  id: number
  name: string
  lang: string
}

type NativeAudioTrack = {
  enabled: boolean
  language?: string
  label?: string
}

type NativeAudioTrackList = {
  length: number
  [index: number]: NativeAudioTrack
}

type VideoWithAudioTracks = HTMLVideoElement & {
  audioTracks?: NativeAudioTrackList
}

function normalizeTrackName(name: string, fallbackIndex: number) {
  const trimmed = name.trim()
  return trimmed || `Track ${fallbackIndex + 1}`
}

function findEnglishTrackId(tracks: AudioTrackOption[]) {
  return (
    tracks.find((track) => /^(en|eng|english)$/i.test(track.lang.trim()))?.id ??
    tracks.find((track) => /english/i.test(track.name))?.id ??
    null
  )
}

function readNativeAudioTracks(video: HTMLVideoElement): AudioTrackOption[] {
  const nativeTracks = (video as VideoWithAudioTracks).audioTracks
  if (!nativeTracks || nativeTracks.length === 0) {
    return []
  }

  return Array.from({ length: nativeTracks.length }, (_, index) => {
    const track = nativeTracks[index]
    return {
      id: index,
      name: normalizeTrackName(track?.label ?? '', index),
      lang: track?.language ?? '',
    }
  })
}

function isHlsStreamUrl(streamUrl: string) {
  const normalizedUrl = streamUrl.trim().toLowerCase()

  if (!normalizedUrl) {
    return false
  }

  try {
    const parsedUrl = new URL(normalizedUrl)
    return parsedUrl.pathname.endsWith('.m3u8')
  } catch {
    return normalizedUrl.includes('.m3u8')
  }
}

function resetVideoElement(video: HTMLVideoElement) {
  video.pause()
  video.removeAttribute('src')
  video.load()
}

function getErrorMessage(error: unknown, fallback: string) {
  if (error instanceof Error && error.message) {
    return error.message
  }

  if (typeof error === 'string' && error.trim()) {
    return error
  }

  if (error && typeof error === 'object') {
    const message = Reflect.get(error, 'message')
    if (typeof message === 'string' && message.trim()) {
      return message
    }

    try {
      const serialized = JSON.stringify(error)
      if (serialized && serialized !== '{}') {
        return serialized
      }
    } catch {
      // Ignore serialization errors and fall back.
    }
  }

  return fallback
}

const EXTERNAL_PLAYER_STORAGE_KEY = 'xtream-player.external-player-preference'

export function PlayerPage({ state, dispatch }: PlayerPageProps) {
  const session = state.playerSession
  const playerEmbedRef = useRef<HTMLDivElement>(null)
  const videoRef = useRef<HTMLVideoElement>(null)
  const hlsRef = useRef<Hls | null>(null)
  const toastTimeoutRef = useRef<number | null>(null)
  const nativeTrackPollRef = useRef<number | null>(null)
  const embeddedLaunchUrlRef = useRef<string | null>(null)
  const [playerStatus, setPlayerStatus] = useState<PlayerStatus | null>(null)
  const [statusMessage, setStatusMessage] = useState('')
  const [statusTone, setStatusTone] = useState<StatusTone>('idle')
  const [showStatusToast, setShowStatusToast] = useState(false)
  const [isPlaying, setIsPlaying] = useState(false)
  const [externalPlayerPreference, setExternalPlayerPreference] = useState<ExternalPlayerPreference>(() => {
    if (typeof window === 'undefined') {
      return 'vlc'
    }

    const stored = window.localStorage.getItem(EXTERNAL_PLAYER_STORAGE_KEY)
    return stored === 'mpv' || stored === 'auto' || stored === 'vlc' ? stored : 'vlc'
  })

  const supportsResume = useMemo(
    () => session?.streamType === 'vod' || session?.streamType === 'series',
    [session],
  )

  const hasPlaylist = !!(session?.episodePlaylist && session.episodePlaylist.length > 1)
  const currentIndex = session?.currentEpisodeIndex ?? 0
  const hasPrevEpisode = hasPlaylist && currentIndex > 0
  const hasNextEpisode = hasPlaylist && currentIndex < (session?.episodePlaylist?.length ?? 0) - 1
  const isEmbeddedVlcActive = playerStatus?.transport === 'managed-vlc-embedded'
  const nativeAudioCycleAvailable =
    playerStatus?.transport === 'managed-mpv-ipc' ||
    playerStatus?.transport === 'managed-vlc-embedded'

  function measureEmbeddedBounds() {
    const embed = playerEmbedRef.current
    if (!embed) {
      return null
    }

    const rect = embed.getBoundingClientRect()
    if (rect.width < 1 || rect.height < 1) {
      return null
    }

    const scale = window.devicePixelRatio || 1
    return {
      x: Math.round(rect.left * scale),
      y: Math.round(rect.top * scale),
      width: Math.max(1, Math.round(rect.width * scale)),
      height: Math.max(1, Math.round(rect.height * scale)),
    }
  }

  async function launchEmbeddedVlc(showSuccessMessage: boolean) {
    if (!session) return

    const bounds = measureEmbeddedBounds()
    if (!bounds) {
      setStatusTone('error')
      setStatusMessage('The player area is not ready for embedded VLC yet.')
      return
    }

    const video = videoRef.current
    if (video) {
      video.pause()
      setIsPlaying(false)
    }

    try {
      const status = await startNativeVlcWindowPlayback(session.title, session.streamUrl, bounds)
      embeddedLaunchUrlRef.current = session.streamUrl
      setPlayerStatus(status)
      setIsPlaying(true)
      if (showSuccessMessage) {
        setStatusTone('success')
        setStatusMessage('Embedded VLC is active in the player panel. English audio will be preferred when available.')
      }
    } catch (error) {
      const message = getErrorMessage(error, 'Embedded VLC launch failed')
      setStatusTone('error')
      setStatusMessage(message)
    }
  }

  useEffect(() => {
    if (!statusMessage || statusTone === 'idle') {
      setShowStatusToast(false)
      if (toastTimeoutRef.current !== null) {
        window.clearTimeout(toastTimeoutRef.current)
        toastTimeoutRef.current = null
      }
      return
    }

    setShowStatusToast(true)

    if (toastTimeoutRef.current !== null) {
      window.clearTimeout(toastTimeoutRef.current)
    }

    toastTimeoutRef.current = window.setTimeout(() => {
      setShowStatusToast(false)
      toastTimeoutRef.current = null
    }, statusTone === 'error' ? 6500 : 3500)

    return () => {
      if (toastTimeoutRef.current !== null) {
        window.clearTimeout(toastTimeoutRef.current)
        toastTimeoutRef.current = null
      }
    }
  }, [statusMessage, statusTone])

  useEffect(() => {
    if (!session?.streamUrl) {
      embeddedLaunchUrlRef.current = null
      return
    }

    if (!isEmbeddedVlcActive) {
      embeddedLaunchUrlRef.current = null
      return
    }

    if (embeddedLaunchUrlRef.current === session.streamUrl) {
      return
    }

    void launchEmbeddedVlc(false)
  }, [isEmbeddedVlcActive, session?.streamUrl])

  useEffect(() => {
    if (!isEmbeddedVlcActive) {
      return
    }

    let frameId = 0

    const syncBounds = () => {
      frameId = window.requestAnimationFrame(() => {
        const bounds = measureEmbeddedBounds()
        if (!bounds) {
          return
        }

        void syncEmbeddedVlcBounds(bounds).catch(() => {})
      })
    }

    const resizeObserver = new ResizeObserver(() => {
      if (frameId) {
        window.cancelAnimationFrame(frameId)
      }
      syncBounds()
    })

    const embed = playerEmbedRef.current
    if (embed) {
      resizeObserver.observe(embed)
    }

    const handleResize = () => {
      if (frameId) {
        window.cancelAnimationFrame(frameId)
      }
      syncBounds()
    }

    window.addEventListener('resize', handleResize)
    syncBounds()

    return () => {
      if (frameId) {
        window.cancelAnimationFrame(frameId)
      }
      resizeObserver.disconnect()
      window.removeEventListener('resize', handleResize)
    }
  }, [isEmbeddedVlcActive, session?.streamUrl])

  useEffect(() => {
    if (!session?.streamUrl) {
      void getPlayerStatus().then(setPlayerStatus)
      return
    }

    const video = videoRef.current
    if (!video) return

    if (nativeTrackPollRef.current !== null) {
      window.clearInterval(nativeTrackPollRef.current)
      nativeTrackPollRef.current = null
    }

    function applyPreferredAudioTrack(nextTracks: AudioTrackOption[]) {
      if (nextTracks.length === 0) {
        return
      }

      const preferredTrackId = findEnglishTrackId(nextTracks) ?? nextTracks[0]?.id ?? 0
      selectAudioTrack(preferredTrackId)
    }

    function syncNativeTracks() {
      if (!videoRef.current) {
        return false
      }

      const nextTracks = readNativeAudioTracks(videoRef.current)
      if (nextTracks.length === 0) {
        return false
      }

      applyPreferredAudioTrack(nextTracks)

      if (nativeTrackPollRef.current !== null) {
        window.clearInterval(nativeTrackPollRef.current)
        nativeTrackPollRef.current = null
      }

      return true
    }

    function startNativeTrackPolling() {
      let attempts = 0
      nativeTrackPollRef.current = window.setInterval(() => {
        attempts += 1
        const foundTracks = syncNativeTracks()

        if (foundTracks) {
          if (nativeTrackPollRef.current !== null) {
            window.clearInterval(nativeTrackPollRef.current)
            nativeTrackPollRef.current = null
          }
          return
        }

        if (attempts >= 20) {
          if (nativeTrackPollRef.current !== null) {
            window.clearInterval(nativeTrackPollRef.current)
            nativeTrackPollRef.current = null
          }
        }
      }, 250)
    }

    // Cleanup previous HLS instance
    if (hlsRef.current) {
      hlsRef.current.destroy()
      hlsRef.current = null
    }

    resetVideoElement(video)

    let cancelled = false
    let onLoadedMetadata: (() => void) | null = null
    let onVideoError: (() => void) | null = null

    void (async () => {
      const shouldUseHls = isHlsStreamUrl(session.streamUrl)
      const playbackUrl = await resolveEmbeddedStreamUrl(session.streamUrl).catch(() => session.streamUrl)
      if (cancelled) {
        return
      }

      if (shouldUseHls) {
        if (Hls.isSupported()) {
          let hasRetriedDirectHls = false

          const attachHlsSource = (sourceUrl: string, sourceLabel: 'proxy' | 'direct') => {
            const hls = new Hls({
              enableWorker: true,
              lowLatencyMode: true,
            })
            hlsRef.current = hls
            hls.loadSource(sourceUrl)
            hls.attachMedia(video)
            hls.on(Hls.Events.MANIFEST_PARSED, () => {
              video.play().catch(() => {})
              setIsPlaying(true)
              setStatusTone('success')
              setStatusMessage(`Playing: ${session.title}`)
              const tracks = hls.audioTracks.map((t, i) => ({
                id: i,
                name: normalizeTrackName(t.name || '', i),
                lang: t.lang || '',
              }))
              applyPreferredAudioTrack(tracks)
            })
            hls.on(Hls.Events.AUDIO_TRACKS_UPDATED, () => {
              const tracks = hls.audioTracks.map((t, i) => ({
                id: i,
                name: normalizeTrackName(t.name || '', i),
                lang: t.lang || '',
              }))
              applyPreferredAudioTrack(tracks)
            })
            hls.on(Hls.Events.ERROR, (_event, data) => {
              if (!data.fatal) {
                return
              }

              const statusCode = data.response && 'code' in data.response ? data.response.code : null

              const shouldRetryDirect =
                sourceLabel === 'proxy' &&
                !hasRetriedDirectHls &&
                data.details === Hls.ErrorDetails.MANIFEST_LOAD_ERROR &&
                statusCode === 0 &&
                sourceUrl !== session.streamUrl

              if (shouldRetryDirect) {
                hasRetriedDirectHls = true
                hls.destroy()
                if (hlsRef.current === hls) {
                  hlsRef.current = null
                }
                attachHlsSource(session.streamUrl, 'direct')
                return
              }

              setStatusTone('error')
              setStatusMessage(
                `HLS error: ${data.details}${typeof statusCode === 'number' ? ` (${statusCode})` : ''}. Try MPV or the external player if this stream blocks browser playback.`,
              )
            })
          }

          attachHlsSource(playbackUrl, playbackUrl === session.streamUrl ? 'direct' : 'proxy')
        } else if (video.canPlayType('application/vnd.apple.mpegurl')) {
          video.src = playbackUrl
          video.load()
          onLoadedMetadata = () => {
            video.play().catch(() => {})
            setIsPlaying(true)
            setStatusTone('success')
            setStatusMessage(`Playing: ${session.title}`)
            syncNativeTracks()
            startNativeTrackPolling()
          }
          video.addEventListener('loadedmetadata', onLoadedMetadata)
        }
      } else {
        video.src = playbackUrl
        video.load()
        onLoadedMetadata = () => {
          video.play().catch(() => {})
          setIsPlaying(true)
          setStatusTone('success')
          setStatusMessage(`Playing: ${session.title}`)
          syncNativeTracks()
          startNativeTrackPolling()
        }
        onVideoError = () => {
          setStatusTone('error')
          setStatusMessage('Stream playback error. Try MPV or the external player.')
        }
        video.addEventListener('loadedmetadata', onLoadedMetadata)
        video.addEventListener('error', onVideoError)
      }
    })()

    return () => {
      cancelled = true
      if (nativeTrackPollRef.current !== null) {
        window.clearInterval(nativeTrackPollRef.current)
        nativeTrackPollRef.current = null
      }

      if (onLoadedMetadata) {
        video.removeEventListener('loadedmetadata', onLoadedMetadata)
      }

      if (onVideoError) {
        video.removeEventListener('error', onVideoError)
      }

      if (hlsRef.current) {
        hlsRef.current.destroy()
        hlsRef.current = null
      }

      resetVideoElement(video)
    }
  }, [session])

  function handlePlayPause() {
    const video = videoRef.current
    if (!video) return
    if (video.paused) {
      video.play().catch(() => {})
      setIsPlaying(true)
    } else {
      video.pause()
      setIsPlaying(false)
    }
  }

  function handleStop() {
    const video = videoRef.current
    if (!video) return
    resetVideoElement(video)
    setIsPlaying(false)
    if (hlsRef.current) {
      hlsRef.current.destroy()
      hlsRef.current = null
    }
    void stopInternalPlayback().then(setPlayerStatus).catch(() => {})
    setStatusTone('idle')
    setStatusMessage('Playback stopped.')
  }

  async function handleInternalPlayerLaunch() {
    if (!session) return

    const video = videoRef.current
    if (video) {
      video.pause()
      setIsPlaying(false)
    }

    try {
      const status = await startInternalPlayback(session.title, session.streamUrl)
      setPlayerStatus(status)
      setStatusTone('success')
      setStatusMessage('Opened managed internal player. English audio will be preferred when available.')
    } catch (error) {
      const message = getErrorMessage(error, 'Internal player launch failed')
      setStatusTone('error')
      setStatusMessage(message)
    }
  }

  async function handleInternalAudioCycle() {
    try {
      const status = await cycleInternalAudioTrack()
      setPlayerStatus(status)
      setStatusTone('success')
      setStatusMessage('Switched internal player audio track.')
    } catch (error) {
      const message = getErrorMessage(error, 'Internal audio switch failed')
      setStatusTone('error')
      setStatusMessage(message)
    }
  }

  async function handleExternalFallback() {
    if (!session) return
    try {
      const status = await openExternalPlayer(session.streamUrl, externalPlayerPreference)
      setPlayerStatus(status)
      setStatusTone('success')
      setStatusMessage(`Launched ${externalPlayerPreference === 'auto' ? 'external player' : externalPlayerPreference.toUpperCase()}.`)
    } catch (error) {
      const message = getErrorMessage(error, 'External fallback failed')
      setStatusTone('error')
      setStatusMessage(message)
    }
  }

  useEffect(() => {
    if (typeof window === 'undefined') {
      return
    }

    window.localStorage.setItem(EXTERNAL_PLAYER_STORAGE_KEY, externalPlayerPreference)
  }, [externalPlayerPreference])

  function handleBack() {
    void stopInternalPlayback().catch(() => {})
    embeddedLaunchUrlRef.current = null
    const video = videoRef.current
    if (video) {
      resetVideoElement(video)
      if (hlsRef.current) {
        hlsRef.current.destroy()
        hlsRef.current = null
      }
    }
    const target = state.previousView ?? 'dashboard'
    dispatch({ type: 'navigate', view: target })
  }

  function handleEpisodeNav(direction: 'prev' | 'next') {
    if (!session?.episodePlaylist) return
    const newIndex = direction === 'next' ? currentIndex + 1 : currentIndex - 1
    const ep = session.episodePlaylist[newIndex]
    if (!ep) return

    dispatch({
      type: 'startPlayback',
      session: {
        ...session,
        title: ep.title,
        streamUrl: ep.streamUrl,
        currentEpisodeIndex: newIndex,
      },
    })
  }

  function selectAudioTrack(trackId: number) {
    if (hlsRef.current) {
      hlsRef.current.audioTrack = trackId
      return
    }

    const nativeTracks = (videoRef.current as VideoWithAudioTracks | null)?.audioTracks
    if (nativeTracks && nativeTracks.length > 0) {
      for (let index = 0; index < nativeTracks.length; index += 1) {
        const track = nativeTracks[index]
        if (track) {
          track.enabled = index === trackId
        }
      }
    }
  }

  return (
    <div className="view-stack player-view">
      {showStatusToast ? (
        <div className="player-toast">
          <StatusBanner tone={statusTone} message={statusMessage} />
        </div>
      ) : null}

      <div ref={playerEmbedRef} className={`player-embed${isEmbeddedVlcActive ? ' player-embed-native' : ''}`}>
        <video
          ref={videoRef}
          className={`player-video${isEmbeddedVlcActive ? ' player-video-hidden' : ''}`}
          controls
          playsInline
        />
      </div>

      <div className="player-controls-bar">
        <div className="player-info">
          <h2 className="player-title">{session?.title ?? 'No stream selected'}</h2>
          {session && (
            <span className="player-meta">
              {session.streamType} &middot; {isPlaying ? 'Playing' : 'Paused'}
            </span>
          )}
          <p className="player-helper-copy">
            For more options like audio tracks, captions, and advanced playback controls, use the MPV or external player buttons.
          </p>
        </div>

        <div className="player-toolbar">
          <button type="button" className="toolbar-btn" onClick={handleBack} title="Back">
            ←
          </button>
          {hasPrevEpisode && (
            <button type="button" className="toolbar-btn" onClick={() => handleEpisodeNav('prev')} title="Previous episode">
              ⏮
            </button>
          )}
          <button type="button" className="toolbar-btn" onClick={handlePlayPause} title={isPlaying ? 'Pause' : 'Play'}>
            {isPlaying ? '⏸' : '▶'}
          </button>
          <button type="button" className="toolbar-btn" onClick={handleStop} title="Stop">
            ⏹
          </button>
          {hasNextEpisode && (
            <button type="button" className="toolbar-btn" onClick={() => handleEpisodeNav('next')} title="Next episode">
              ⏭
            </button>
          )}
          <button type="button" className="toolbar-btn" onClick={handleExternalFallback} title="External player">
            🖥
          </button>
          <button
            type="button"
            className="toolbar-btn"
            onClick={() => void handleInternalPlayerLaunch()}
            title="Managed internal player"
          >
            MPV
          </button>
          <button
            type="button"
            className="toolbar-btn"
            onClick={() => void handleInternalAudioCycle()}
            title="Cycle native audio track"
            disabled={!nativeAudioCycleAvailable}
          >
            🔊
          </button>
          {supportsResume && session?.streamUrl && (
            <button
              type="button"
              className="toolbar-btn"
              title="Download"
              onClick={() => window.open(session.streamUrl, '_blank')}
            >
              ⬇
            </button>
          )}
        </div>

        <div className="audio-track-picker external-player-picker">
          <label htmlFor="external-player-select">🖥 External app</label>
          <select
            id="external-player-select"
            value={externalPlayerPreference}
            onChange={(event) => setExternalPlayerPreference(event.target.value as ExternalPlayerPreference)}
          >
            <option value="vlc">VLC</option>
            <option value="mpv">MPV</option>
            <option value="auto">Auto</option>
          </select>
        </div>
      </div>
    </div>
  )
}