using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LibVLCSharp.Avalonia;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Playback;

namespace VideoSpike;

/// <summary>
/// Phase 0 harness. Throwaway: delete this project after Phase 5.
/// </summary>
/// <remarks>
/// Deliberately code-behind with no MVVM. Its only job is to answer, with a
/// real stream, whether embedded video and track switching work on Avalonia 11
/// before any of the real UI gets built on that assumption.
/// </remarks>
public partial class MainWindow : Window
{
    private readonly LibVlcEngine _engine;
    private readonly VlcPlaybackService _playback;
    private readonly DispatcherTimer _statusTimer;

    private bool _suppressTrackSelection;
    private bool _userIsDraggingSeek;

    public MainWindow()
    {
        // InitializeComponent(), NOT AvaloniaXamlLoader.Load(this). Both parse
        // the XAML, but only InitializeComponent assigns the generated x:Name
        // fields — with Load() every named control stays null and the first use
        // is a NullReferenceException.
        InitializeComponent();

        // Diagnostics on from Phase 0: provider-side stream failures are
        // undiagnosable without the libvlc log, and retrofitting it while
        // debugging a live issue is miserable.
        _engine = new LibVlcEngine(new LibVlcOptions { Diagnostics = true });

        // Constructed on the UI thread so the captured SynchronizationContext
        // is the UI one and every event arrives already marshalled.
        _playback = new VlcPlaybackService(_engine);

        _playback.StateChanged += OnPlaybackStateChanged;
        _playback.PositionChanged += OnPositionChanged;
        _playback.TracksChanged += OnTracksChanged;
        _playback.Error += OnPlaybackError;

        Video.MediaPlayer = _playback.MediaPlayer;

        VolumeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
            {
                _playback.Volume = (int)VolumeSlider.Value;
            }
        };

        SeekSlider.AddHandler(PointerPressedEvent, (_, _) => _userIsDraggingSeek = true,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        SeekSlider.AddHandler(PointerReleasedEvent, OnSeekReleased,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();

        Closing += OnClosing;
    }

    // ---- Source ---------------------------------------------------------

    private async void OnOpenClicked(object? sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            StatusText.Text = "Enter a stream URL first.";
            return;
        }

        var kind = KindBox.SelectedIndex == 0 ? ContentKind.Live : ContentKind.Vod;
        await OpenAsync(url, kind, Path.GetFileName(url));
    }

    private async void OnOpenFileClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Pick a local video file",
            AllowMultiple = false,
        });

        if (files.Count == 0)
        {
            return;
        }

        var file = files[0];
        await OpenAsync(file.Path.AbsoluteUri, ContentKind.Vod, file.Name);
    }

    private async Task OpenAsync(string url, ContentKind kind, string title)
    {
        TitleText.Text = title;

        // Live is never seekable — hide the seek bar entirely rather than
        // showing a dead control.
        var isLive = kind == ContentKind.Live;
        SeekSlider.IsVisible = !isLive;
        PositionText.IsVisible = !isLive;
        DurationText.IsVisible = !isLive;

        try
        {
            await _playback.OpenAsync(new PlaybackRequest(url, title, kind));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Open failed: {ex.Message}";
        }
    }

    // ---- Transport ------------------------------------------------------

    private async void OnPlayPauseClicked(object? sender, RoutedEventArgs e) =>
        await _playback.TogglePauseAsync();

    private async void OnStopClicked(object? sender, RoutedEventArgs e) =>
        await _playback.StopAsync();

    private async void OnBack10Clicked(object? sender, RoutedEventArgs e) =>
        await _playback.SeekRelativeAsync(TimeSpan.FromSeconds(-10));

    private async void OnForward10Clicked(object? sender, RoutedEventArgs e) =>
        await _playback.SeekRelativeAsync(TimeSpan.FromSeconds(10));

    private async void OnSeekReleased(object? sender, RoutedEventArgs e)
    {
        _userIsDraggingSeek = false;

        var duration = _playback.Duration;
        if (duration > TimeSpan.Zero)
        {
            var fraction = SeekSlider.Value / SeekSlider.Maximum;
            await _playback.SeekAsync(duration * fraction);
        }
    }

    // ---- Tracks ---------------------------------------------------------

    private void OnTracksChanged(object? sender, EventArgs e)
    {
        _suppressTrackSelection = true;
        try
        {
            AudioBox.ItemsSource = _playback.AudioTracks.Select(t => t.Label).ToList();
            AudioBox.SelectedIndex = IndexOfTrack(_playback.AudioTracks, _playback.SelectedAudioTrackId);

            SubtitleBox.ItemsSource = _playback.SubtitleTracks.Select(t => t.Label).ToList();
            SubtitleBox.SelectedIndex =
                IndexOfTrack(_playback.SubtitleTracks, _playback.SelectedSubtitleTrackId);
        }
        finally
        {
            _suppressTrackSelection = false;
        }
    }

    private static int IndexOfTrack(IReadOnlyList<TrackOption> tracks, int id)
    {
        for (var i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private void OnAudioSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressTrackSelection || AudioBox.SelectedIndex < 0)
        {
            return;
        }

        _playback.SetAudioTrack(_playback.AudioTracks[AudioBox.SelectedIndex].Id);
    }

    private void OnSubtitleSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressTrackSelection || SubtitleBox.SelectedIndex < 0)
        {
            return;
        }

        _playback.SetSubtitleTrack(_playback.SubtitleTracks[SubtitleBox.SelectedIndex].Id);
    }

    private async void OnLoadSubtitleClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Pick a subtitle file",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Subtitles") { Patterns = ["*.srt", "*.ass", "*.ssa", "*.sub", "*.vtt"] },
            ],
        });

        if (files.Count == 0)
        {
            return;
        }

        var ok = await _playback.LoadExternalSubtitleAsync(files[0].Path.AbsoluteUri);
        StatusText.Text = ok
            ? $"Loaded subtitle: {files[0].Name}"
            : $"AddSlave rejected: {files[0].Name}";
    }

    // ---- The airspace and layout-dodge probes ---------------------------

    private void OnAutoHideToggled(object? sender, RoutedEventArgs e)
    {
        // The dodge: collapse the chrome rows so the video expands to fill.
        var hide = AutoHideToggle.IsChecked == true;
        TitleChrome.IsVisible = !hide;
        TransportChrome.IsVisible = !hide;
    }

    private void OnOverlayToggled(object? sender, RoutedEventArgs e) =>
        OverlayProbe.IsVisible = OverlayToggle.IsChecked == true;

    private void OnFullscreenToggled(object? sender, RoutedEventArgs e) =>
        WindowState = FullscreenToggle.IsChecked == true
            ? WindowState.FullScreen
            : WindowState.Normal;

    // ---- Instrumentation ------------------------------------------------

    private void OnPlaybackStateChanged(object? sender, PlaybackStateChangedEventArgs e) =>
        UpdateStatus();

    private void OnPositionChanged(object? sender, TimeSpan position)
    {
        PositionText.Text = Format(position);

        var duration = _playback.Duration;
        DurationText.Text = Format(duration);

        if (!_userIsDraggingSeek && duration > TimeSpan.Zero)
        {
            SeekSlider.Value = position / duration * SeekSlider.Maximum;
        }
    }

    private void OnPlaybackError(object? sender, PlaybackErrorEventArgs e)
    {
        StatusText.Text = $"ERROR [{e.Kind}] {e.Message}";
        Console.Error.WriteLine(e.Message);
        Console.Error.WriteLine(e.Detail);
    }

    private void UpdateStatus() =>
        StatusText.Text =
            $"{_playback.State,-9} | seekable={_playback.IsSeekable,-5} | "
          + $"audio={_playback.AudioTracks.Count} (sel {_playback.SelectedAudioTrackId}) | "
          + $"subs={_playback.SubtitleTracks.Count} (sel {_playback.SelectedSubtitleTrackId}) | "
          + $"vol={_playback.Volume} | libvlc={VlcLocator.ResolvedDirectory}";

    private static string Format(TimeSpan value) => value.TotalHours >= 1
        ? value.ToString(@"h\:mm\:ss")
        : value.ToString(@"m\:ss");

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _statusTimer.Stop();

        // Verifies the documented teardown order actually lets the process
        // exit: stop, detach, dispose media, dispose player, dispose LibVLC.
        await _playback.DisposeAsync();
        _engine.Dispose();
    }
}
