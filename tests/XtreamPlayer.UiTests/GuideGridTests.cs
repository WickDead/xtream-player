using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;
using XtreamPlayer.App.Controls;
using XtreamPlayer.Core.Models;

namespace XtreamPlayer.UiTests;

/// <summary>
/// The guide grid, which is drawn by hand rather than built from controls.
/// </summary>
/// <remarks>
/// A thousand channels over thirty hours is well past a hundred thousand
/// programme blocks, so the grid renders only the visible rectangle and computes
/// every coordinate itself. That means raw arithmetic on user-controlled values —
/// a zoom level, a scroll offset, a window length — with no framework between it
/// and a divide by zero. None of it was covered.
/// </remarks>
public sealed class GuideGridTests
{
    private static EpgRow Row(string id, int programmes = 20)
    {
        var start = DateTimeOffset.Now.AddHours(-2);

        return new EpgRow(id, $"Channel {id}", [.. Enumerable.Range(0, programmes).Select(i =>
            new EpgProgramme
            {
                XmltvChannelId = id,
                Title = $"Programme {i}",
                Start = start.AddMinutes(i * 30),
                Stop = start.AddMinutes((i + 1) * 30),
            })]);
    }

    private static (Window Window, EpgTimelineCanvas Canvas) Build(IReadOnlyList<EpgRow> rows)
    {
        var canvas = new EpgTimelineCanvas
        {
            Rows = rows,
            WindowStart = DateTimeOffset.Now.AddHours(-2),
            WindowLength = TimeSpan.FromHours(30),
            PixelsPerMinute = 6,
        };

        var window = new Window
        {
            Width = 1000,
            Height = 600,
            Content = new ScrollViewer { Content = canvas },
        };

        window.Show();
        Settle(window);

        return (window, canvas);
    }

    /// <summary>
    /// Lays out, drains the dispatcher, and forces a render.
    /// </summary>
    /// <remarks>
    /// The render tick matters more than the rest. This grid does all its work
    /// in Render, and a test that never triggers one passes without touching the
    /// code it claims to cover — which is exactly what happened: a zoom of zero
    /// threw from inside the render pass and the Debug run reported green
    /// because the renderer had not got to it.
    /// </remarks>
    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void DrawsAGuideWithChannelsInIt()
    {
        var (window, canvas) = Build([.. Enumerable.Range(0, 200).Select(i => Row($"ch{i}"))]);

        Assert.Equal(200, canvas.Rows.Count);

        window.Close();
    }

    [AvaloniaFact]
    public void SurvivesHavingNoChannelsAtAll()
    {
        // What a provider with no XMLTV feed produces, and what the grid shows
        // for a category whose channels all lack listings.
        var (window, _) = Build([]);

        window.Close();
    }

    [AvaloniaFact]
    public void SurvivesChannelsWithNoListings()
    {
        var (window, _) = Build([.. Enumerable.Range(0, 50).Select(i => Row($"ch{i}", programmes: 0))]);

        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(0)]          // a zoom level that would divide by zero
    [InlineData(-1)]         // nonsense, but reachable through a binding
    [InlineData(0.0001)]
    [InlineData(10000)]
    public void SurvivesAnyZoomLevel(double pixelsPerMinute)
    {
        var (window, canvas) = Build([.. Enumerable.Range(0, 20).Select(i => Row($"ch{i}"))]);

        canvas.PixelsPerMinute = pixelsPerMinute;
        Settle(window);

        window.Close();
    }

    [AvaloniaFact]
    public void SurvivesAZeroLengthWindow()
    {
        var (window, canvas) = Build([.. Enumerable.Range(0, 20).Select(i => Row($"ch{i}"))]);

        canvas.WindowLength = TimeSpan.Zero;
        Settle(window);

        window.Close();
    }

    [AvaloniaFact]
    public void ClampsScrollingBeyondTheEnds()
    {
        var (window, canvas) = Build([.. Enumerable.Range(0, 30).Select(i => Row($"ch{i}"))]);

        canvas.Offset = new Vector(-10_000, -10_000);
        Settle(window);

        Assert.True(canvas.Offset.X >= 0 && canvas.Offset.Y >= 0,
            $"Negative offset survived: {canvas.Offset}");

        canvas.Offset = new Vector(10_000_000, 10_000_000);
        Settle(window);

        Assert.True(canvas.Offset.X <= canvas.Extent.Width
                 && canvas.Offset.Y <= canvas.Extent.Height,
            $"Offset ran past the extent: {canvas.Offset} vs {canvas.Extent}");

        window.Close();
    }

    [AvaloniaFact]
    public void ShrinksTheOffsetWhenTheRowsDo()
    {
        // Filtering the guide to a small category while scrolled far down: the
        // offset used to stay past the new extent, leaving the grid blank with
        // nothing to explain it.
        var (window, canvas) = Build([.. Enumerable.Range(0, 2000).Select(i => Row($"ch{i}"))]);

        canvas.Offset = new Vector(0, 50_000);
        Settle(window);

        Assert.True(canvas.Offset.Y > 0);

        canvas.Rows = [.. Enumerable.Range(0, 3).Select(i => Row($"ch{i}"))];
        Settle(window);

        Assert.True(canvas.Offset.Y <= canvas.Extent.Height,
            $"Offset {canvas.Offset.Y} is past the new extent {canvas.Extent.Height}.");

        window.Close();
    }

    [AvaloniaFact]
    public void HandlesClicksAnywhereIncludingPastTheLastRow()
    {
        var (window, canvas) = Build([.. Enumerable.Range(0, 5).Select(i => Row($"ch{i}"))]);

        canvas.Focus();

        // Well below five rows of content, and off the right-hand end.
        foreach (var point in new[]
        {
            new Point(10, 10),
            new Point(500, 300),
            new Point(999, 599),
            new Point(1, 1),
        })
        {
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Settle(window);
        }

        window.Close();
    }

    [AvaloniaFact]
    public void HandlesKeyboardNavigationAtTheExtremes()
    {
        var (window, canvas) = Build([.. Enumerable.Range(0, 10).Select(i => Row($"ch{i}"))]);

        canvas.Focus();

        foreach (var key in new[]
        {
            Key.Home, Key.Left, Key.Left, Key.Up, Key.Up, Key.PageUp,
            Key.End, Key.Right, Key.Down, Key.PageDown, Key.PageDown,
        })
        {
            window.KeyPress(key, RawInputModifiers.None);
            Settle(window);
        }

        Assert.True(canvas.Offset.X >= 0 && canvas.Offset.Y >= 0);

        window.Close();
    }
}
