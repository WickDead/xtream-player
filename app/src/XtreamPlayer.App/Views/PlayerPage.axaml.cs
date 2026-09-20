using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Messaging;
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

        PointerPressed += (_, e) =>
        {
            ShowChrome();

            // Double-click the picture to toggle fullscreen. Ignored on the
            // controls themselves, where a double-click on a button or the seek
            // bar means something else entirely.
            if (e.ClickCount == 2 && e.Source is not Button and not Slider and not ComboBox)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
        };

        SeekSlider.AddHandler(PointerPressedEvent, OnSeekPressed, RoutingStrategies.Tunnel);
        SeekSlider.AddHandler(PointerReleasedEvent, OnSeekReleased, RoutingStrategies.Tunnel);

        LoadSubtitleButton.Click += OnLoadSubtitle;
        FullscreenButton.Click += (_, _) => ToggleFullscreen();

        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += (_, e) =>
        {
            _hideChrome.Stop();

            // Drop the video surface first. LibVLCSharp hosts the floating OSD
            // in a separate top-most window and keeps it aligned to this
            // control from the parent window's message loop; once the control
            // leaves the visual tree that alignment calls PointToScreen on a
            // rootless visual and throws from inside WndProc, where nothing can
            // catch it and the process simply dies. Clearing these tears the
            // overlay down before it can fire again.
            Video.Content = null;
            Video.MediaPlayer = null;

            // Going Back out of a fullscreen video would otherwise leave the
            // window chromeless with no player in it: no sidebar, no
            // breadcrumbs, and nothing on screen explaining how to get them
            // back.
            //
            // The window comes from the event rather than from this control:
            // by the time a detach runs, GetTopLevel can already return null,
            // and bailing out here is precisely how the chrome would stay
            // hidden with nothing to restore it.
            RestoreFromFullscreen(e.Root as Window);
        };

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

    /// <summary>
    /// What the window was before fullscreen, so leaving it restores that
    /// rather than always dropping to a restored window.
    /// </summary>
    private WindowState _stateBeforeFullscreen = WindowState.Maximized;

    private void ToggleFullscreen() => SetFullscreen(!IsFullscreen);

    private static bool IsFullscreenWindow(Visual visual) =>
        TopLevel.GetTopLevel(visual) is Window { WindowState: WindowState.FullScreen };

    private bool IsFullscreen => IsFullscreenWindow(this);

    /// <summary>
    /// Takes the window fullscreen and asks the shell to give up its chrome.
    /// </summary>
    /// <remarks>
    /// Both halves are needed. The window state alone leaves the sidebar,
    /// breadcrumb bar and status bar holding their space, so the video ends up
    /// letterboxed inside a chromeless window instead of filling the screen —
    /// which is what "fullscreen" did before.
    /// </remarks>
    /// <summary>
    /// Unconditionally returns the shell to its normal chrome.
    /// </summary>
    /// <remarks>
    /// The message is sent whether or not a window was found, because the shell
    /// hiding its chrome is the part the user cannot undo on their own.
    /// </remarks>
    private void RestoreFromFullscreen(Window? window)
    {
        // Immediately, and whether or not a window was found: the shell hiding
        // its chrome is the part the user cannot undo on their own.
        WeakReferenceMessenger.Default.Send(new FullscreenChangedMessage(false));

        if (window is not { WindowState: WindowState.FullScreen })
        {
            return;
        }

        var target = _stateBeforeFullscreen == WindowState.FullScreen
            ? WindowState.Maximized
            : _stateBeforeFullscreen;

        // Posted rather than assigned here. Leaving fullscreen resizes the
        // window synchronously, and doing that while the visual tree is still
        // coming apart is what drove the overlay crash above.
        Dispatcher.UIThread.Post(
            () => window.WindowState = target, DispatcherPriority.Background);
    }

    private void SetFullscreen(bool fullscreen)
    {
        if (TopLevel.GetTopLevel(this) is not Window window)
        {
            return;
        }

        var already = window.WindowState == WindowState.FullScreen;

        if (already == fullscreen)
        {
            // Still re-assert the shell's state: it can drift if the window left
            // fullscreen by some route other than this method.
            WeakReferenceMessenger.Default.Send(new FullscreenChangedMessage(fullscreen));
            return;
        }

        if (fullscreen)
        {
            _stateBeforeFullscreen = window.WindowState;
            window.WindowState = WindowState.FullScreen;
        }
        else
        {
            window.WindowState = _stateBeforeFullscreen == WindowState.FullScreen
                ? WindowState.Maximized
                : _stateBeforeFullscreen;
        }

        WeakReferenceMessenger.Default.Send(new FullscreenChangedMessage(fullscreen));

        if (fullscreen)
        {
            // The video's native child window takes focus when it resizes, and
            // without this the keyboard shortcuts stop reaching this page.
            Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Loaded);
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
                if (IsFullscreen)
                {
                    SetFullscreen(false);
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
