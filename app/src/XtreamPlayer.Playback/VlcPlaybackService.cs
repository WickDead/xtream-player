using System.Globalization;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using XtreamPlayer.Core.Models;

namespace XtreamPlayer.Playback;

public sealed class PlaybackPreferences
{
    /// <summary>
    /// Checked against MediaTrack.Language first, then as a substring of the
    /// track description. Empty disables the preference entirely.
    /// </summary>
    public IReadOnlyList<string> PreferredAudioLanguages { get; set; } = ["eng", "en"];

    /// <summary>Empty means "leave subtitles off", which is the default.</summary>
    public IReadOnlyList<string> PreferredSubtitleLanguages { get; set; } = [];

    /// <summary>Live streams need a much larger buffer than VOD.</summary>
    public int LiveNetworkCachingMs { get; set; } = 3000;

    public int VodNetworkCachingMs { get; set; } = 1000;

    /// <summary>Automatic reconnect attempts for live streams before giving up.</summary>
    public int LiveRetryAttempts { get; set; } = 2;

    public TimeSpan LiveRetryBackoff { get; set; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// The single playback engine, over LibVLCSharp.
/// </summary>
/// <remarks>
/// <para>Threading rules, learned the hard way and enforced throughout:</para>
/// <list type="bullet">
/// <item>Never call a MediaPlayer method from inside a libvlc event handler.
/// libvlc holds a lock for the duration of the callback and re-entering
/// deadlocks. Every handler here does nothing but hop off the callback
/// thread.</item>
/// <item><c>Stop()</c> blocks until the decoder thread joins — hundreds of ms.
/// It always runs on the thread pool. Calling it on the UI thread is the
/// classic "app freezes when I close the player" bug.</item>
/// <item>TimeChanged fires roughly 4x/sec; PositionChanged is throttled to
/// 1 Hz for binding.</item>
/// </list>
/// </remarks>
public sealed class VlcPlaybackService : IPlaybackService
{
    private static readonly TrackOption SubtitleOff =
        new(-1, "Off", null, null, IsDefault: true);

    private readonly LibVlcEngine _engine;
    private readonly PlaybackPreferences _preferences;
    private readonly SynchronizationContext? _eventContext;
    private readonly SemaphoreSlim _openGate = new(1, 1);

    private readonly MediaPlayer _mediaPlayer;
    private Media? _currentMedia;

    private PlaybackState _state = PlaybackState.Idle;
    private long _lastRaisedPositionSeconds = -1;
    private TimeSpan? _pendingResumeSeek;
    private int _liveRetriesRemaining;
    private int _retryInFlight;
    private bool _preferencesApplied;
    private CancellationTokenSource? _lateTrackScanCts;
    private bool _disposed;

    public VlcPlaybackService(LibVlcEngine engine, PlaybackPreferences? preferences = null)
    {
        _engine = engine;
        _preferences = preferences ?? new PlaybackPreferences();

        // Captured so events reach the caller's thread. A service resolved on
        // the UI thread raises on the UI thread, which is what the ViewModels
        // want; nothing here ever raises from a libvlc callback thread.
        _eventContext = SynchronizationContext.Current;

        // ONE long-lived MediaPlayer, reused across every stream. Creating one
        // per stream churns the native video surface, which is where the
        // intermittent black-frame and lost-audio bugs come from.
        _mediaPlayer = new MediaPlayer(engine.LibVlc);
        Subscribe(_mediaPlayer);
    }

    /// <summary>
    /// Exposed so the Avalonia VideoView can attach. Do not call playback
    /// methods on it directly — go through the service.
    /// </summary>
    public MediaPlayer MediaPlayer => _mediaPlayer;

    public PlaybackState State => _state;

    public TimeSpan Position => _mediaPlayer.Time < 0
        ? TimeSpan.Zero
        : TimeSpan.FromMilliseconds(_mediaPlayer.Time);

    public TimeSpan Duration => _mediaPlayer.Length <= 0
        ? TimeSpan.Zero
        : TimeSpan.FromMilliseconds(_mediaPlayer.Length);

    public bool IsSeekable => Current?.IsLive != true && _mediaPlayer.IsSeekable;

    public int Volume
    {
        get => _mediaPlayer.Volume;
        set => _mediaPlayer.Volume = Math.Clamp(value, 0, 100);
    }

    public bool IsMuted
    {
        get => _mediaPlayer.Mute;
        set => _mediaPlayer.Mute = value;
    }

    public IReadOnlyList<TrackOption> AudioTracks { get; private set; } = [];

    public IReadOnlyList<TrackOption> SubtitleTracks { get; private set; } = [SubtitleOff];

    public int SelectedAudioTrackId => _mediaPlayer.AudioTrack;

    public int SelectedSubtitleTrackId => _mediaPlayer.Spu;

    public PlaybackRequest? Current { get; private set; }

    public event EventHandler<PlaybackStateChangedEventArgs>? StateChanged;
    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler? TracksChanged;
    public event EventHandler<PlaybackErrorEventArgs>? Error;

    // ---- Opening --------------------------------------------------------

    public async Task OpenAsync(PlaybackRequest request, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _openGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);

            Current = request;
            _liveRetriesRemaining = request.IsLive ? _preferences.LiveRetryAttempts : 0;
            _pendingResumeSeek = null;
            _lastRaisedPositionSeconds = -1;
            _preferencesApplied = false;

            AudioTracks = [];
            SubtitleTracks = [SubtitleOff];
            RaiseTracksChanged();

            SetState(PlaybackState.Opening);

            var media = BuildMedia(request);

            var previous = _currentMedia;
            _currentMedia = media;
            _mediaPlayer.Media = media;

            // Safe and required: the MediaPlayer took its own reference.
            previous?.Dispose();

            if (!_mediaPlayer.Play())
            {
                SetState(PlaybackState.Error);
                RaiseError(new PlaybackErrorEventArgs(
                    PlaybackErrorKind.DecodeOrConnection,
                    "Playback could not be started.",
                    _engine.LogTail()));
            }
        }
        finally
        {
            _openGate.Release();
        }
    }

    private Media BuildMedia(PlaybackRequest request)
    {
        var media = new Media(_engine.LibVlc, new Uri(request.Url));

        // Caching is set per-media, not globally: live needs a big buffer and a
        // relaxed clock, VOD wants to start fast.
        if (request.IsLive)
        {
            media.AddOption($":network-caching={_preferences.LiveNetworkCachingMs}");
            media.AddOption($":live-caching={_preferences.LiveNetworkCachingMs}");
            media.AddOption(":clock-jitter=0");
            media.AddOption(":clock-synchro=0");
        }
        else
        {
            media.AddOption($":network-caching={_preferences.VodNetworkCachingMs}");
            media.AddOption(":file-caching=300");
        }

        if (request.StartAt is { TotalSeconds: > 1 } startAt && !request.IsLive)
        {
            // Preferred over seeking: decoding begins at the offset with no
            // visible seek. Some containers ignore it, so OnPlaying also holds a
            // fallback — seeking BEFORE the Playing event is silently dropped.
            var seconds = startAt.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);
            media.AddOption($":start-time={seconds}");
            _pendingResumeSeek = startAt;
        }

        return media;
    }

    // ---- Transport ------------------------------------------------------

    public Task PlayAsync()
    {
        if (_mediaPlayer.Media is not null && !_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Play();
        }

        return Task.CompletedTask;
    }

    public Task PauseAsync()
    {
        if (_mediaPlayer.CanPause && _mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Pause();
        }

        return Task.CompletedTask;
    }

    public Task TogglePauseAsync()
    {
        if (_mediaPlayer.CanPause)
        {
            _mediaPlayer.Pause();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync() => StopCoreAsync();

    private async Task StopCoreAsync()
    {
        _lateTrackScanCts?.Cancel();

        if (_mediaPlayer.Media is null)
        {
            return;
        }

        // Blocks until the decoder thread joins. Never on the UI thread.
        var player = _mediaPlayer;
        await Task.Run(() => player.Stop()).ConfigureAwait(false);

        SetState(PlaybackState.Idle);
    }

    public Task SeekAsync(TimeSpan position)
    {
        if (!IsSeekable)
        {
            return Task.CompletedTask;
        }

        var clamped = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        if (Duration > TimeSpan.Zero && clamped > Duration)
        {
            clamped = Duration;
        }

        _mediaPlayer.Time = (long)clamped.TotalMilliseconds;
        return Task.CompletedTask;
    }

    public Task SeekRelativeAsync(TimeSpan delta) => SeekAsync(Position + delta);

    // ---- Tracks ---------------------------------------------------------

    /// <remarks>
    /// The id that ends up selected is NOT always the id requested. On HLS with
    /// alternate audio renditions libvlc tears down the current audio ES and
    /// builds a new one, which gets a fresh id from a monotonic counter —
    /// observed switching to id 2 and landing on id 12. So: re-scan afterwards,
    /// and never persist a preference keyed on a raw ES id (persist language
    /// and track name instead).
    /// </remarks>
    public void SetAudioTrack(int id)
    {
        _mediaPlayer.SetAudioTrack(id);
        ScheduleTrackRefresh(applyPreferences: false);
    }

    /// <inheritdoc cref="SetAudioTrack" />
    public void SetSubtitleTrack(int id)
    {
        _mediaPlayer.SetSpu(id);
        ScheduleTrackRefresh(applyPreferences: false);
    }

    public Task<bool> LoadExternalSubtitleAsync(string pathOrUri, bool select = true)
    {
        var uri = Uri.TryCreate(pathOrUri, UriKind.Absolute, out var parsed)
            ? parsed.AbsoluteUri
            : new Uri(Path.GetFullPath(pathOrUri)).AbsoluteUri;

        var added = _mediaPlayer.AddSlave(MediaSlaveType.Subtitle, uri, select);

        if (added)
        {
            ScheduleTrackRefresh(applyPreferences: false);
        }

        return Task.FromResult(added);
    }

    /// <summary>
    /// Rebuilds the track lists from the MediaPlayer's descriptions joined
    /// against the Media's elementary-stream metadata.
    /// </summary>
    /// <remarks>
    /// Never call this from a libvlc callback — it reads MediaPlayer state.
    /// </remarks>
    private void RefreshTracks()
    {
        if (_disposed)
        {
            return;
        }

        // TrackDescription carries the selectable id and a display name;
        // MediaTrack carries Language and Codec. Join on id for a real label
        // ("English (AC-3 5.1)") instead of the raw description string the
        // previous app showed.
        var mediaTracks = _currentMedia?.Tracks ?? [];

        var audio = new List<TrackOption>();
        foreach (var description in _mediaPlayer.AudioTrackDescription)
        {
            if (description.Id < 0)
            {
                continue;
            }

            audio.Add(BuildTrackOption(description, mediaTracks, TrackType.Audio));
        }

        var subtitles = new List<TrackOption> { SubtitleOff };
        foreach (var description in _mediaPlayer.SpuDescription)
        {
            if (description.Id < 0)
            {
                // libvlc already exposes "Disable" as -1; SubtitleOff replaces it.
                continue;
            }

            subtitles.Add(BuildTrackOption(description, mediaTracks, TrackType.Text));
        }

        var changed = !SequenceEquivalent(AudioTracks, audio)
                      || !SequenceEquivalent(SubtitleTracks, subtitles);

        AudioTracks = audio;
        SubtitleTracks = subtitles;

        if (changed)
        {
            RaiseTracksChanged();
        }
    }

    private static bool SequenceEquivalent(IReadOnlyList<TrackOption> a, IReadOnlyList<TrackOption> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Id != b[i].Id || !string.Equals(a[i].Label, b[i].Label, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static TrackOption BuildTrackOption(
        TrackDescription description,
        MediaTrack[] mediaTracks,
        TrackType type)
    {
        MediaTrack? match = null;
        foreach (var track in mediaTracks)
        {
            if (track.TrackType == type && track.Id == description.Id)
            {
                match = track;
                break;
            }
        }

        var language = Normalize(match?.Language);
        var codec = match is null ? null : FourCc.Describe(match.Value.Codec, match.Value.OriginalFourcc);

        var name = Normalize(description.Name)
                   ?? Normalize(match?.Description)
                   ?? language
                   ?? $"Track {description.Id}";

        var label = codec is null ? name : $"{name} ({codec})";

        return new TrackOption(description.Id, label, language, codec, IsDefault: false);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Applies the preferred-audio-language rule.
    /// </summary>
    /// <remarks>
    /// Improves on the previous app's <c>prefer_english_audio</c>
    /// (src-tauri/src/api/vlc.rs:343), which only did the substring match and
    /// ignored the track's actual language tag — the correct signal.
    /// </remarks>
    private void ApplyPreferredTracks()
    {
        if (_preferencesApplied)
        {
            return;
        }

        _preferencesApplied = true;

        ApplyPreferred(
            AudioTracks,
            _preferences.PreferredAudioLanguages,
            SetAudioTrack);

        if (_preferences.PreferredSubtitleLanguages.Count > 0)
        {
            ApplyPreferred(
                SubtitleTracks.Where(t => t.Id >= 0).ToList(),
                _preferences.PreferredSubtitleLanguages,
                SetSubtitleTrack);
        }
    }

    private static void ApplyPreferred(
        IReadOnlyList<TrackOption> tracks,
        IReadOnlyList<string> preferredLanguages,
        Action<int> select)
    {
        if (tracks.Count < 2 || preferredLanguages.Count == 0)
        {
            return;
        }

        // 1. The track's own language tag — the correct signal.
        foreach (var language in preferredLanguages)
        {
            foreach (var track in tracks)
            {
                if (track.Language is { } tag
                    && tag.StartsWith(language, StringComparison.OrdinalIgnoreCase))
                {
                    select(track.Id);
                    return;
                }
            }
        }

        // 2. Substring of the label, which is what the old Rust code did.
        foreach (var language in preferredLanguages)
        {
            foreach (var track in tracks)
            {
                if (track.Label.Contains(language, StringComparison.OrdinalIgnoreCase))
                {
                    select(track.Id);
                    return;
                }
            }
        }

        // 3. Otherwise leave libvlc's default alone.
    }

    /// <summary>
    /// Re-scans tracks shortly after playback starts. Some providers add
    /// elementary streams late, after the Playing event and after the initial
    /// ESAdded burst.
    /// </summary>
    private void ScheduleTrackRefresh(bool applyPreferences)
    {
        _lateTrackScanCts?.Cancel();
        _lateTrackScanCts?.Dispose();

        var cts = new CancellationTokenSource();
        _lateTrackScanCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1500), cts.Token).ConfigureAwait(false);
                Post(() =>
                {
                    RefreshTracks();

                    if (applyPreferences)
                    {
                        ApplyPreferredTracks();
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer open, or we stopped.
            }
        });
    }

    // ---- libvlc events --------------------------------------------------
    //
    // Every handler below runs on a libvlc thread while libvlc holds a lock.
    // They must not touch the MediaPlayer. All they do is hop off the callback.

    private void Subscribe(MediaPlayer player)
    {
        player.Opening += OnOpening;
        player.Buffering += OnBuffering;
        player.Playing += OnPlaying;
        player.Paused += OnPaused;
        player.Stopped += OnStopped;
        player.EndReached += OnEndReached;
        player.EncounteredError += OnEncounteredError;
        player.TimeChanged += OnTimeChanged;
        player.ESAdded += OnElementaryStreamsChanged;
        player.ESSelected += OnElementaryStreamSelected;
    }

    private void Unsubscribe(MediaPlayer player)
    {
        player.Opening -= OnOpening;
        player.Buffering -= OnBuffering;
        player.Playing -= OnPlaying;
        player.Paused -= OnPaused;
        player.Stopped -= OnStopped;
        player.EndReached -= OnEndReached;
        player.EncounteredError -= OnEncounteredError;
        player.TimeChanged -= OnTimeChanged;
        player.ESAdded -= OnElementaryStreamsChanged;
        player.ESSelected -= OnElementaryStreamSelected;
    }

    private void OnOpening(object? sender, EventArgs e) => Post(() => SetState(PlaybackState.Opening));

    private void OnBuffering(object? sender, MediaPlayerBufferingEventArgs e)
    {
        if (e.Cache < 100f)
        {
            Post(() => SetState(PlaybackState.Buffering));
        }
    }

    private void OnPlaying(object? sender, EventArgs e) => Post(() =>
    {
        SetState(PlaybackState.Playing);

        // Fallback for containers that ignore :start-time. Seeking before the
        // Playing event is silently dropped, so it has to happen here.
        if (_pendingResumeSeek is { } target)
        {
            _pendingResumeSeek = null;

            if (IsSeekable && Position < target - TimeSpan.FromSeconds(5))
            {
                _mediaPlayer.Time = (long)target.TotalMilliseconds;
            }
        }

        RefreshTracks();
        ApplyPreferredTracks();
        ScheduleTrackRefresh(applyPreferences: true);
    });

    private void OnPaused(object? sender, EventArgs e) => Post(() => SetState(PlaybackState.Paused));

    private void OnStopped(object? sender, EventArgs e) => Post(() =>
    {
        if (_state is not (PlaybackState.Ended or PlaybackState.Error))
        {
            SetState(PlaybackState.Idle);
        }
    });

    private void OnEndReached(object? sender, EventArgs e) => Post(() => SetState(PlaybackState.Ended));

    private void OnEncounteredError(object? sender, EventArgs e) => PostAsync(async () =>
    {
        // libvlc raises EncounteredError several times for a single failure.
        // Without this guard, each one starts its own reconnect.
        if (Interlocked.CompareExchange(ref _retryInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            // Xtream servers routinely drop live connections when
            // max_connections is exceeded, and a silent reconnect covers most
            // of it.
            if (Current is { IsLive: true } request && _liveRetriesRemaining > 0)
            {
                _liveRetriesRemaining--;
                SetState(PlaybackState.Buffering);

                await Task.Delay(_preferences.LiveRetryBackoff).ConfigureAwait(false);

                try
                {
                    await OpenAsync(request with { StartAt = null }).ConfigureAwait(false);
                    _liveRetriesRemaining = 0;
                }
                catch (Exception ex)
                {
                    RaiseError(new PlaybackErrorEventArgs(
                        PlaybackErrorKind.DecodeOrConnection,
                        "Reconnecting to the stream failed.",
                        ex.Message));
                }

                return;
            }

            SetState(PlaybackState.Error);
            RaiseError(new PlaybackErrorEventArgs(
                PlaybackErrorKind.DecodeOrConnection,
                "The stream could not be played.",
                _engine.LogTail()));
        }
        finally
        {
            Interlocked.Exchange(ref _retryInFlight, 0);
        }
    });

    private void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        // Fires ~4x/sec. Throttle to 1 Hz — the UI shows whole seconds and
        // resume checkpointing runs on its own 10 s timer.
        var seconds = e.Time / 1000;
        if (seconds == _lastRaisedPositionSeconds)
        {
            return;
        }

        _lastRaisedPositionSeconds = seconds;
        var position = TimeSpan.FromMilliseconds(e.Time);
        Post(() => PositionChanged?.Invoke(this, position));
    }

    private void OnElementaryStreamsChanged(object? sender, MediaPlayerESAddedEventArgs e) =>
        Post(RefreshTracks);

    private void OnElementaryStreamSelected(object? sender, MediaPlayerESSelectedEventArgs e) =>
        Post(RefreshTracks);

    // ---- Plumbing -------------------------------------------------------

    private void SetState(PlaybackState next)
    {
        if (_state == next)
        {
            return;
        }

        var previous = _state;
        _state = next;
        StateChanged?.Invoke(this, new PlaybackStateChangedEventArgs(previous, next));
    }

    private void RaiseTracksChanged() => TracksChanged?.Invoke(this, EventArgs.Empty);

    private void RaiseError(PlaybackErrorEventArgs args) => Error?.Invoke(this, args);

    /// <summary>
    /// Hops off the libvlc callback thread onto the captured context.
    /// </summary>
    private void Post(Action action)
    {
        if (_disposed)
        {
            return;
        }

        if (_eventContext is null)
        {
            Task.Run(action);
            return;
        }

        _eventContext.Post(_ => action(), null);
    }

    /// <summary>
    /// Fire-and-forget variant for async handlers.
    /// </summary>
    /// <remarks>
    /// The block body is load-bearing. Written as an expression lambda
    /// (<c>() =&gt; _ = action()</c>) the assignment's value is a Task, so the
    /// lambda binds as Func&lt;Task&gt; and this method calls ITSELF forever —
    /// a stack overflow the first time a libvlc error fires. A statement body
    /// makes the lambda void-returning, so it binds to Post(Action).
    /// </remarks>
    private void PostAsync(Func<Task> action) => Post(() => { _ = action(); });

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _lateTrackScanCts?.Cancel();
        _lateTrackScanCts?.Dispose();

        var player = _mediaPlayer;
        Unsubscribe(player);

        // Order matters: stop, detach from the view, dispose media, dispose the
        // player, and only then the LibVLC instance (owned by LibVlcEngine).
        // Getting it wrong leaves orphan threads and the process will not exit.
        await Task.Run(() =>
        {
            player.Stop();
            player.Media = null;
            player.Dispose();
        }).ConfigureAwait(false);

        _currentMedia?.Dispose();
        _currentMedia = null;

        _openGate.Dispose();
    }
}
