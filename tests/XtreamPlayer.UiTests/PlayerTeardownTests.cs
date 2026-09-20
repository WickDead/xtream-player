using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using XtreamPlayer.App.Views;

namespace XtreamPlayer.UiTests;

/// <summary>
/// The seam where the player comes apart.
/// </summary>
/// <remarks>
/// Two crashes have shipped from here: a resume checkpoint that threw on its way
/// out of the page, and the video overlay repositioning against a control that
/// had already left the visual tree. Both needed a real control really attached
/// and then really removed, which is the one thing pure-logic tests cannot do.
/// </remarks>
public sealed class PlayerTeardownTests
{
    /// <summary>
    /// Lets posted work run, which is where teardown bugs surface.
    /// </summary>
    /// <remarks>
    /// The player defers several things to the dispatcher — handing the
    /// MediaPlayer to the video surface, restoring the window state — so a test
    /// that returns without draining the queue never reaches the code that
    /// actually broke.
    /// </remarks>
    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();

            // Forced, not awaited: teardown bugs in this app have surfaced from
            // inside the render pass, and a test that never renders never sees
            // them.
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ThePlayerPageCanBeShownAndRemovedWithoutThrowing()
    {
        var window = new Window { Width = 1280, Height = 720 };
        var page = new PlayerPage();

        window.Content = page;
        window.Show();

        Pump();

        // The detach path: exactly what a Back out of the player does.
        window.Content = null;

        Pump();

        window.Close();

        Pump();
    }

    /// <summary>
    /// The real order: the view model says it is leaving, then the view detaches.
    /// </summary>
    /// <remarks>
    /// Releasing the video surface is what this is really testing. Doing it on
    /// detach threw <c>Nullable object must have a value</c> out of
    /// PointToScreen, because the overlay realigns on the property change and a
    /// rootless control cannot say where it is. Doing it here, while the view is
    /// still in the tree, is the fix.
    /// </remarks>
    [AvaloniaFact]
    public void ReleasingTheVideoSurfaceBeforeDetachDoesNotThrow()
    {
        var window = new Window { Width = 1280, Height = 720 };
        var page = new PlayerPage();

        window.Content = page;
        window.Show();

        Pump();

        // What OnNavigatingFromAsync triggers, before the page is popped.
        page.ReleaseVideoSurfaceForTesting();

        Pump();

        window.Content = null;

        Pump();

        window.Close();
    }

    [AvaloniaFact]
    public void ThePlayerSurvivesBeingShownAndRemovedRepeatedly()
    {
        // Ten cycles, because a teardown that leaks or double-frees tends to
        // look fine once and fail on the second or third pass.
        var window = new Window { Width = 1280, Height = 720 };
        window.Show();

        for (var i = 0; i < 10; i++)
        {
            window.Content = new PlayerPage();
            Pump();

            window.Content = null;
            Pump();
        }

        window.Close();
    }

    [AvaloniaFact]
    public void TheWindowCanResizeWhileThePlayerIsBeingRemoved()
    {
        // The shape of the crash that shipped: leaving fullscreen resizes the
        // window, and doing that as the page detaches had the video overlay
        // realigning against a visual that no longer had a root.
        var window = new Window { Width = 1280, Height = 720 };
        window.Content = new PlayerPage();
        window.Show();

        Pump();

        window.Content = null;

        // Resize in the same breath as the detach, before posted work drains.
        window.Width = 900;
        window.Height = 600;

        Pump();

        window.Close();
    }
}
