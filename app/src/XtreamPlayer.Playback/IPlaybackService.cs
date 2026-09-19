namespace XtreamPlayer.Playback;

/// <summary>
/// The single playback engine. Replaces the previous app's three parallel
/// half-finished paths (HTML5+hls.js over a manifest-rewriting proxy, an
/// orphaned libvlc FFI/HWND experiment, and detached mpv over a named pipe).
/// </summary>
/// <remarks>
/// Events are raised on the <see cref="SynchronizationContext"/> captured when
/// the service was constructed, so a service resolved on the UI thread raises
/// on the UI thread. They are never raised from inside a libvlc callback.
/// </remarks>
public interface IPlaybackService : IAsyncDisposable
{
    PlaybackState State { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    bool IsSeekable { get; }

    /// <summary>0-100.</summary>
    int Volume { get; set; }
    bool IsMuted { get; set; }

    IReadOnlyList<TrackOption> AudioTracks { get; }

    /// <summary>Always includes a synthetic "Off" option with id -1.</summary>
    IReadOnlyList<TrackOption> SubtitleTracks { get; }

    int SelectedAudioTrackId { get; }
    int SelectedSubtitleTrackId { get; }

    PlaybackRequest? Current { get; }

    Task OpenAsync(PlaybackRequest request, CancellationToken ct = default);
    Task PlayAsync();
    Task PauseAsync();
    Task TogglePauseAsync();
    Task StopAsync();
    Task SeekAsync(TimeSpan position);
    Task SeekRelativeAsync(TimeSpan delta);

    void SetAudioTrack(int id);
    void SetSubtitleTrack(int id);
    Task<bool> LoadExternalSubtitleAsync(string pathOrUri, bool select = true);

    event EventHandler<PlaybackStateChangedEventArgs>? StateChanged;

    /// <summary>Throttled to roughly 1 Hz; libvlc fires ~4x/sec.</summary>
    event EventHandler<TimeSpan>? PositionChanged;

    event EventHandler? TracksChanged;
    event EventHandler<PlaybackErrorEventArgs>? Error;
}
