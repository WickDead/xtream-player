using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using XtreamPlayer.App.ViewModels;
using XtreamPlayer.Playback;

namespace XtreamPlayer.App.Views;

public partial class PlayerPage : UserControl
{
    /// <summary>How long the chrome stays up after the pointer stops moving.</summary>
    private static readonly TimeSpan ChromeTimeout = TimeSpan.FromSeconds(3);

    private readonly DispatcherTimer _hideChrome;
    private bool _draggingSeek;

    public PlayerPage()
    {
        InitializeComponent();

        _hideChrome = new DispatcherTimer { Interval = ChromeTimeout };
        _hideChrome.Tick += (_, _) =>
        {
            _hideChrome.Stop();

            // Never hide the chrome while something is wrong: the error panel
            // and its Retry button must stay reachable.
            if (DataContext is PlayerPageViewModel { ErrorMessage: null, State: PlaybackState.Playing } vm)
            {
                vm.ChromeVisible = false;
            }
        };

        PointerMoved += (_, _) => ShowChrome();
        PointerPressed += (_, _) => ShowChrome();

        SeekSlider.AddHandler(PointerPressedEvent, OnSeekPressed, RoutingStrategies.Tunnel);
        SeekSlider.AddHandler(PointerReleasedEvent, OnSeekReleased, RoutingStrategies.Tunnel);

        LoadSubtitleButton.Click += OnLoadSubtitle;
        FullscreenButton.Click += (_, _) => ToggleFullscreen();

        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += (_, _) => _hideChrome.Stop();

        KeyDown += OnKeyDown;
        Focusable = true;
    }

    /// <summary>
    /// Hands the MediaPlayer to the VideoView once its native handle exists.
    /// </summary>
    /// <remarks>
    /// Timing is everything and failure is silent. LibVLCSharp.Avalonia 3.10.1
    /// only assigns MediaPlayer.Hwnd inside a private Attach() that requires its
    /// platform handle to be non-null — and the method that creates that handle
    /// never calls Attach() afterwards. Attach() is otherwise reached only from
    /// the Initialized event and the MediaPlayer setter, both of which fire
    /// BEFORE the handle exists. Assign it in XAML or a constructor and libvlc
    /// quietly opens its own separate window instead of drawing into ours.
    /// </remarks>
    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is PlayerPageViewModel vm
                && vm.Playback is VlcPlaybackService service)
            {
                Video.MediaPlayer = service.MediaPlayer;
            }

            Focus();
            ShowChrome();
        }, DispatcherPriority.Loaded);
    }

    private void ShowChrome()
    {
        if (DataContext is PlayerPageViewModel vm)
        {
            vm.ChromeVisible = true;
        }

        _hideChrome.Stop();
        _hideChrome.Start();
    }

    private void OnSeekPressed(object? sender, PointerPressedEventArgs e) => _draggingSeek = true;

    private async void OnSeekReleased(object? sender, PointerReleasedEventArgs e)
    {
        _draggingSeek = false;

        if (DataContext is PlayerPageViewModel vm)
        {
            await vm.SeekToFractionAsync(SeekSlider.Value / SeekSlider.Maximum);
        }
    }

    private async void OnLoadSubtitle(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PlayerPageViewModel vm
            || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load a subtitle file",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Subtitles")
                {
                    Patterns = ["*.srt", "*.ass", "*.ssa", "*.sub", "*.vtt"],
                },
            ],
        });

        if (files.Count > 0)
        {
            await vm.LoadSubtitleFileAsync(files[0].Path.AbsoluteUri);
        }
    }

    private void ToggleFullscreen()
    {
        if (TopLevel.GetTopLevel(this) is Window window)
        {
            window.WindowState = window.WindowState == WindowState.FullScreen
                ? WindowState.Normal
                : WindowState.FullScreen;
        }
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not PlayerPageViewModel vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                await vm.TogglePauseCommand.ExecuteAsync(null);
                e.Handled = true;
                break;

            case Key.Left when !vm.IsLive:
                await vm.SeekBackCommand.ExecuteAsync(null);
                e.Handled = true;
                break;

            case Key.Right when !vm.IsLive:
                await vm.SeekForwardCommand.ExecuteAsync(null);
                e.Handled = true;
                break;

            case Key.M:
                vm.ToggleMuteCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.F:
                ToggleFullscreen();
                e.Handled = true;
                break;

            // First Escape leaves fullscreen, second closes the player. Jumping
            // straight out of a fullscreen video is disorienting.
            case Key.Escape:
                if (TopLevel.GetTopLevel(this) is Window { WindowState: WindowState.FullScreen })
                {
                    ToggleFullscreen();
                }
                else
                {
                    await vm.CloseCommand.ExecuteAsync(null);
                }

                e.Handled = true;
                break;
        }

        ShowChrome();
    }
}
