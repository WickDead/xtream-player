import { useEffect, useMemo, useRef, useState } from 'react'
import Hls from 'hls.js'
import { StatusBanner, type StatusTone } from '../../components/StatusBanner'
import {
  getPlayerStatus,
  openExternalPlayer,
  stopInternalPlayback,
} from '../../lib/backend'
import type { AppAction } from '../../stores/appStore'
import type { AppState, PlayerStatus } from '../../types/xtream'

interface PlayerPageProps {
  state: AppState
  dispatch: React.Dispatch<AppAction>
  refreshBootstrap: () => Promise<void>
}

export function PlayerPage({ state, dispatch }: PlayerPageProps) {
  const session = state.playerSession
  const videoRef = useRef<HTMLVideoElement>(null)
  const hlsRef = useRef<Hls | null>(null)
  const [, setPlayerStatus] = useState<PlayerStatus | null>(null)
  const [statusMessage, setStatusMessage] = useState('')
  const [statusTone, setStatusTone] = useState<StatusTone>('idle')
  const [isPlaying, setIsPlaying] = useState(false)
  const [audioTracks, setAudioTracks] = useState<{ id: number; name: string; lang: string }[]>([])
  const [activeAudioTrack, setActiveAudioTrack] = useState(0)

  const supportsResume = useMemo(
    () => session?.streamType === 'vod' || session?.streamType === 'series',
    [session],
  )

  const hasPlaylist = !!(session?.episodePlaylist && session.episodePlaylist.length > 1)
  const currentIndex = session?.currentEpisodeIndex ?? 0
  const hasPrevEpisode = hasPlaylist && currentIndex > 0
  const hasNextEpisode = hasPlaylist && currentIndex < (session?.episodePlaylist?.length ?? 0) - 1

  useEffect(() => {
    if (!session?.streamUrl) {
      void getPlayerStatus().then(setPlayerStatus)
      return
    }

    const video = videoRef.current
    if (!video) return

    const url = session.streamUrl

    // Cleanup previous HLS instance
    if (hlsRef.current) {
      hlsRef.current.destroy()
      hlsRef.current = null
    }

    if (url.endsWith('.m3u8') || url.includes('.m3u8')) {
      if (Hls.isSupported()) {
        const hls = new Hls({
          enableWorker: true,
          lowLatencyMode: true,
        })
        hlsRef.current = hls
        hls.loadSource(url)
        hls.attachMedia(video)
        hls.on(Hls.Events.MANIFEST_PARSED, () => {
          video.play().catch(() => {})
          setIsPlaying(true)
          setStatusTone('success')
          setStatusMessage(`Playing: ${session.title}`)
          // Detect audio tracks
          const tracks = hls.audioTracks.map((t, i) => ({
            id: i,
            name: t.name || `Track ${i + 1}`,
            lang: t.lang || '',
          }))
          setAudioTracks(tracks)
          setActiveAudioTrack(hls.audioTrack)
        })
        hls.on(Hls.Events.AUDIO_TRACKS_UPDATED, () => {
          const tracks = hls.audioTracks.map((t, i) => ({
            id: i,
            name: t.name || `Track ${i + 1}`,
            lang: t.lang || '',
          }))
          setAudioTracks(tracks)
          setActiveAudioTrack(hls.audioTrack)
        })
        hls.on(Hls.Events.ERROR, (_event, data) => {
          if (data.fatal) {
            setStatusTone('error')
            setStatusMessage(`HLS error: ${data.details}`)
          }
        })
      } else if (video.canPlayType('application/vnd.apple.mpegurl')) {
        // Native HLS support (Safari)
        video.src = url
        video.addEventListener('loadedmetadata', () => {
          video.play().catch(() => {})
          setIsPlaying(true)
          setStatusTone('success')
          setStatusMessage(`Playing: ${session.title}`)
        })
      }
    } else {
      // Direct MP4/TS stream
      video.src = url
      video.addEventListener('loadedmetadata', () => {
        video.play().catch(() => {})
        setIsPlaying(true)
        setStatusTone('success')
        setStatusMessage(`Playing: ${session.title}`)
      })
      video.addEventListener('error', () => {
        setStatusTone('error')
        setStatusMessage('Stream playback error. Try external player.')
      })
    }

    return () => {
      if (hlsRef.current) {
        hlsRef.current.destroy()
        hlsRef.current = null
      }
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
    video.pause()
    video.currentTime = 0
    setIsPlaying(false)
    if (hlsRef.current) {
      hlsRef.current.destroy()
      hlsRef.current = null
    }
    void stopInternalPlayback().then(setPlayerStatus).catch(() => {})
    setStatusTone('idle')
    setStatusMessage('Playback stopped.')
  }

  async function handleExternalFallback() {
    if (!session) return
    try {
      const status = await openExternalPlayer(session.streamUrl)
      setPlayerStatus(status)
      setStatusTone('success')
      setStatusMessage('Launched external player.')
    } catch (error) {
      const message = error instanceof Error ? error.message : 'External fallback failed'
      setStatusTone('error')
      setStatusMessage(message)
    }
  }

  function handleBack() {
    const video = videoRef.current
    if (video) {
      video.pause()
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

  function handleAudioTrackChange(trackId: number) {
    if (hlsRef.current) {
      hlsRef.current.audioTrack = trackId
      setActiveAudioTrack(trackId)
    }
  }

  return (
    <div className="view-stack player-view">
      <div className="player-embed">
        <video
          ref={videoRef}
          className="player-video"
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
        </div>

        <StatusBanner tone={statusTone} message={statusMessage} />

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

        {audioTracks.length > 1 && (
          <div className="audio-track-picker">
            <label htmlFor="audio-track-select">🔊 Audio</label>
            <select
              id="audio-track-select"
              value={activeAudioTrack}
              onChange={(e) => handleAudioTrackChange(Number(e.target.value))}
            >
              {audioTracks.map((track) => (
                <option key={track.id} value={track.id}>
                  {track.name}{track.lang ? ` (${track.lang})` : ''}
                </option>
              ))}
            </select>
          </div>
        )}
      </div>
    </div>
  )
}