using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using XtreamPlayer.Core.Abstractions;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Core.Navigation;
using XtreamPlayer.Playback;

namespace XtreamPlayer.App.ViewModels;

/// <summary>
/// The player.
/// </summary>
/// <remarks>
/// <para>
/// Disposable, not cached: it owns a media session, and leaving must stop and
/// release it rather than leave a stream running behind a page nobody is
/// looking at. Popping it returns to the list underneath completely untouched —
/// same category, same filter text, same scroll offset — which is the payoff of
/// the cached back stack.
/// </para>
/// <para>
/// Resume checkpointing is wired here. In the previous app the entire resume
/// backend existed — SQLite table, Tauri command, TypeScript binding — and was
/// called from nowhere, so Continue Watching never populated.
/// </para>
/// </remarks>
public sealed partial class PlayerPageViewModel : PageViewModelBase
{
    /// <summary>Frequent enough to be useful, rare enough to be free.</summary>
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(10);

    /// <summary>Below this, an accidental open should not enter Continue Watching.</summary>
    private static readonly TimeSpan MinimumWatchedToRecord = TimeSpan.FromSeconds(30);

    private readonly IPlaybackService _playback;
    private readonly IResumeRepository _resume;
    private readonly IProfileRepository _profiles;
    private readonly ISettingsRepository _settings;
    private readonly INavigationService _navigation;
    private readonly ILogger<PlayerPageViewModel> _log;

    private readonly DispatcherTimer _checkpointTimer;

    private PlaybackRequestParameter? _request;
    private ProfileScope? _scope;
    private TimeSpan _lastCheckpoint = TimeSpan.MinValue;
    private bool _disposed;

    /// <summary>
    /// Set while syncing the combo boxes from the engine, so the setters do not
    /// turn round and ask the engine to switch to the track it just reported.
    /// </summary>
    private bool _syncingTracks;

    public PlayerPageViewModel(
        IPlaybackService playback,
        IResumeRepository resume,
        IProfileRepository profiles,
        ISettingsRepository settings,
        INavigationService navigation,
        ILogger<PlayerPageViewModel> log)
    {
        _playback = playback;
        _resume = resume;
        _profiles = profiles;
        _settings = settings;
        _navigation = navigation;
        _log = log;

        _playback.StateChanged += OnStateChanged;
        _playback.PositionChanged += OnPositionChanged;
        _playback.TracksChanged += OnTracksChanged;
        _playback.Error += OnError;

        _checkpointTimer = new DispatcherTimer { Interval = CheckpointInterval };
        _checkpointTimer.Tick += (_, _) => _ = SaveCheckpointAsync();
    }

    public override PageKey Key => PageKey.Player;

    public override string Title => _request?.Title ?? "Player";

    /// <summary>Never cached: a live media session must not survive a Back.</summary>
    public override NavigationCachePolicy CachePolicy => NavigationCachePolicy.Disposable;

    public IPlaybackService Playback => _playback;

    [ObservableProperty]
    private string _mediaTitle = string.Empty;

    [ObservableProperty]
    private string? _mediaSubtitle;

    [ObservableProperty]
    private PlaybackState _state = PlaybackState.Idle;

    [ObservableProperty]
    private TimeSpan _position;

    [ObservableProperty]
    private TimeSpan _duration;

    [ObservableProperty]
    private bool _isSeekable;

    [ObservableProperty]
    private bool _isLive;

    [ObservableProperty]
    private int _volume = 85;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private bool _chromeVisible = true;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _errorDetail;

    [ObservableProperty]
    private bool _isResumeBannerVisible;

    [ObservableProperty]
    private string _resumeBannerText = string.Empty;

    public ObservableCollection<TrackOption> AudioTracks { get; } = [];

    public ObservableCollection<TrackOption> SubtitleTracks { get; } = [];

    [ObservableProperty]
    private TrackOption? _selectedAudioTrack;

    [ObservableProperty]
    private TrackOption? _selectedSubtitleTrack;

    public bool HasPlaylist => _request?.Playlist is { Count: > 1 };

    public bool CanGoToNextEpisode =>
        _request?.Playlist is { } list && _request.PlaylistIndex < list.Count - 1;

    public bool CanGoToPreviousEpisode => _request?.PlaylistIndex > 0;

    public string PositionLabel => VodDetailPageViewModel.Format(Position);

    public string DurationLabel => VodDetailPageViewModel.Format(Duration);

    /// <summary>0-1000 for the slider, so dragging stays smooth on long titles.</summary>
    public double ProgressValue => Duration > TimeSpan.Zero
        ? Position / Duration * 1000
        : 0;

    public override async Task OnNavigatedToAsync(
        object? parameter, bool isNewInstance, CancellationToken ct = default)
    {
        if (parameter is not PlaybackRequestParameter request)
        {
            ErrorMessage = "Nothing to play.";
            return;
        }

        _request = request;
        MediaTitle = request.Title;
        MediaSubtitle = request.Subtitle;
        IsLive = request.Kind == ContentKind.Live;

        var profile = await ActiveProfileAsync(ct).ConfigureAwait(false);
        _scope = profile is null ? null : new ProfileScope(profile.Id);

        await StartAsync(request, ct).ConfigureAwait(false);
    }

    private async Task StartAsync(PlaybackRequestParameter request, CancellationToken ct)
    {
        ErrorMessage = null;
        ErrorDetail = null;

        try
        {
            _playback.Volume = Volume;

            await _playback.OpenAsync(new PlaybackRequest(
                request.Url,
                request.Title,
                request.Kind,
                request.StartAt,
                request.ContentId), ct).ConfigureAwait(false);

            if (request.StartAt is { TotalSeconds: > 5 } resumeFrom)
            {
                // Shown briefly so resuming is never a surprise, and so starting
                // over is one click away.
                ResumeBannerText = $"Resuming from {VodDetailPageViewModel.Format(resumeFrom)}";
                IsResumeBannerVisible = true;

                _ = HideResumeBannerLaterAsync();
            }

            if (!IsLive)
            {
                _checkpointTimer.Start();
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not start playback.");
            ErrorMessage = "Playback could not be started.";
            ErrorDetail = ex.Message;
        }
    }

    private async Task HideResumeBannerLaterAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(true);
        IsResumeBannerVisible = false;
    }

    // ---- transport ------------------------------------------------------

    [RelayCommand]
    private Task TogglePauseAsync() => _playback.TogglePauseAsync();

    /// <summary>
    /// Seek commands take no parameter, deliberately.
    /// </summary>
    /// <remarks>
    /// A RelayCommand&lt;double&gt; bound to CommandParameter="-10" in XAML
    /// receives the string "-10", and CommunityToolkit throws
    /// ArgumentException when the button attaches to the logical tree. That
    /// happens inside a layout pass, so it is not catchable and takes the
    /// whole app down before the page is even shown. Parameterless commands
    /// remove the entire class of bug.
    /// </remarks>
    [RelayCommand]
    private Task SeekBackAsync() =>
        _playback.SeekRelativeAsync(TimeSpan.FromSeconds(-10));

    /// <inheritdoc cref="SeekBackAsync" />
    [RelayCommand]
    private Task SeekForwardAsync() =>
        _playback.SeekRelativeAsync(TimeSpan.FromSeconds(10));

    /// <summary>Restarts the current title from the beginning.</summary>
    [RelayCommand]
    private async Task StartOverAsync()
    {
        IsResumeBannerVisible = false;
        await _playback.SeekAsync(TimeSpan.Zero).ConfigureAwait(false);
    }

    public Task SeekToFractionAsync(double fraction) =>
        Duration > TimeSpan.Zero
            ? _playback.SeekAsync(Duration * Math.Clamp(fraction, 0, 1))
            : Task.CompletedTask;

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    partial void OnIsMutedChanged(bool value) => _playback.IsMuted = value;

    partial void OnVolumeChanged(int value) => _playback.Volume = value;

    [RelayCommand]
    private async Task CloseAsync() => await _navigation.GoBackAsync().ConfigureAwait(false);

    [RelayCommand]
    private async Task RetryAsync()
    {
        if (_request is not null)
        {
            await StartAsync(_request, CancellationToken.None).ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private async Task NextEpisodeAsync() => await MoveEpisodeAsync(1).ConfigureAwait(false);

    [RelayCommand]
    private async Task PreviousEpisodeAsync() => await MoveEpisodeAsync(-1).ConfigureAwait(false);

    private async Task MoveEpisodeAsync(int delta)
    {
        if (_request?.Playlist is not { } list)
        {
            return;
        }

        var index = _request.PlaylistIndex + delta;
        if (index < 0 || index >= list.Count)
        {
            return;
        }

        // Finish the outgoing episode's checkpoint before switching.
        await SaveCheckpointAsync().ConfigureAwait(false);

        var entry = list[index];
        _request = _request with
        {
            Url = entry.Url,
            Title = entry.Title,
            ContentId = entry.ContentId,
            PlaylistIndex = index,
            StartAt = null,
        };

        MediaTitle = entry.Title;
        _lastCheckpoint = TimeSpan.MinValue;

        OnPropertyChanged(nameof(CanGoToNextEpisode));
        OnPropertyChanged(nameof(CanGoToPreviousEpisode));
        OnPropertyChanged(nameof(Title));

        await StartAsync(_request, CancellationToken.None).ConfigureAwait(false);
    }

    // ---- tracks ---------------------------------------------------------

    private void OnTracksChanged(object? sender, EventArgs e)
    {
        AudioTracks.Clear();
        foreach (var track in _playback.AudioTracks)
        {
            AudioTracks.Add(track);
        }

        SubtitleTracks.Clear();
        foreach (var track in _playback.SubtitleTracks)
        {
            SubtitleTracks.Add(track);
        }

        // Read back from the engine rather than assuming the request was
        // honoured: switching an HLS alternate rendition rebuilds the stream and
        // gives it a fresh id, so the selected id legitimately differs from the
        // one asked for.
        _syncingTracks = true;
        try
        {
            SelectedAudioTrack = AudioTracks
                .FirstOrDefault(t => t.Id == _playback.SelectedAudioTrackId);

            SelectedSubtitleTrack = SubtitleTracks
                .FirstOrDefault(t => t.Id == _playback.SelectedSubtitleTrackId);
        }
        finally
        {
            _syncingTracks = false;
        }
    }

    partial void OnSelectedAudioTrackChanged(TrackOption? value)
    {
        if (!_syncingTracks && value is not null && value.Id != _playback.SelectedAudioTrackId)
        {
            _playback.SetAudioTrack(value.Id);
        }
    }

    partial void OnSelectedSubtitleTrackChanged(TrackOption? value)
    {
        if (!_syncingTracks && value is not null && value.Id != _playback.SelectedSubtitleTrackId)
        {
            _playback.SetSubtitleTrack(value.Id);
        }
    }

    public Task<bool> LoadSubtitleFileAsync(string path) =>
        _playback.LoadExternalSubtitleAsync(path);

    // ---- engine events --------------------------------------------------

    private void OnStateChanged(object? sender, PlaybackStateChangedEventArgs e)
    {
        State = e.Current;
        IsSeekable = _playback.IsSeekable;
        Duration = _playback.Duration;

        OnPropertyChanged(nameof(DurationLabel));

        if (e.Current == PlaybackState.Ended)
        {
            _ = OnEndedAsync();
        }
    }

    private void OnPositionChanged(object? sender, TimeSpan position)
    {
        Position = position;

        OnPropertyChanged(nameof(PositionLabel));
        OnPropertyChanged(nameof(ProgressValue));

        if (Duration != _playback.Duration)
        {
            Duration = _playback.Duration;
            OnPropertyChanged(nameof(DurationLabel));
        }
    }

    private void OnError(object? sender, PlaybackErrorEventArgs e)
    {
        ErrorMessage = e.Message;
        ErrorDetail = e.Detail;
    }

    private async Task OnEndedAsync()
    {
        await MarkFinishedAsync().ConfigureAwait(false);

        // Rolling straight into the next episode is what makes a series binge
        // work without going back to the season list.
        if (CanGoToNextEpisode)
        {
            await MoveEpisodeAsync(1).ConfigureAwait(false);
        }
    }

    // ---- resume ---------------------------------------------------------

    private async Task SaveCheckpointAsync()
    {
        if (_request is null || _scope is not { } scope || IsLive)
        {
            return;
        }

        var position = _playback.Position;
        var duration = _playback.Duration;

        if (duration <= TimeSpan.Zero || position < MinimumWatchedToRecord)
        {
            return;
        }

        // Skip a write when nothing meaningful moved, which happens while paused.
        if ((position - _lastCheckpoint).Duration() < TimeSpan.FromSeconds(5))
        {
            return;
        }

        _lastCheckpoint = position;

        try
        {
            await _resume.SaveAsync(new ResumeItem
            {
                Id = string.Empty,
                ProfileId = scope.Value,
                ContentId = _request.ContentId,
                Kind = _request.Kind,
                Title = _request.Title,
                Detail = _request.Subtitle,
                Position = position,
                Duration = duration,
                Finished = false,
                UpdatedAt = DateTimeOffset.UtcNow,
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Losing a checkpoint must never interrupt playback.
            _log.LogDebug(ex, "Could not save a resume checkpoint.");
        }
    }

    private async Task MarkFinishedAsync()
    {
        if (_request is null || _scope is not { } scope || IsLive)
        {
            return;
        }

        try
        {
            await _resume.SaveAsync(new ResumeItem
            {
                Id = string.Empty,
                ProfileId = scope.Value,
                ContentId = _request.ContentId,
                Kind = _request.Kind,
                Title = _request.Title,
                Detail = _request.Subtitle,
                Position = _playback.Duration,
                Duration = _playback.Duration,
                Finished = true,
                UpdatedAt = DateTimeOffset.UtcNow,
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not mark the item finished.");
        }
    }

    private async Task<Profile?> ActiveProfileAsync(CancellationToken ct)
    {
        var activeId = await _settings.GetAsync(SettingKeys.ActiveProfileId, ct).ConfigureAwait(false);
        var all = await _profiles.ListAsync(ct).ConfigureAwait(false);
        return all.FirstOrDefault(p => p.Id == activeId) ?? all.FirstOrDefault();
    }

    public override async Task OnNavigatingFromAsync(NavigationDirection direction)
    {
        // The last checkpoint, before the session goes away.
        await SaveCheckpointAsync().ConfigureAwait(false);
        await base.OnNavigatingFromAsync(direction).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (disposing)
        {
            _checkpointTimer.Stop();

            _playback.StateChanged -= OnStateChanged;
            _playback.PositionChanged -= OnPositionChanged;
            _playback.TracksChanged -= OnTracksChanged;
            _playback.Error -= OnError;

            // Stop is dispatched rather than awaited: it blocks until the
            // decoder thread joins, and the UI thread must not wait for that.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _playback.StopAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Stopping playback during teardown failed.");
                }
            });
        }

        base.Dispose(disposing);
    }
}
