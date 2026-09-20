using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using XtreamPlayer.App.Behaviors;
using XtreamPlayer.Core.Navigation;

namespace XtreamPlayer.UiTests;

/// <summary>
/// A stand-in page, since only the scroll dictionary matters here.
/// </summary>
internal sealed class FakePage : PageViewModelBase
{
    public override PageKey Key => PageKey.Vod;
    public override string Title => "Fake";
}

/// <summary>
/// A list item, keyed the way the real catalog items are.
/// </summary>
/// <remarks>
/// IScrollKeyed is what lets restoration reconcile by identity rather than
/// index, so an item that moved while the page was away is still found. Without
/// it the anchor is null and only the pixel tier runs.
/// </remarks>
internal sealed record Row(string Id, string Name) : IScrollKeyed
{
    public string ScrollKey => Id;
}

/// <summary>
/// Going back lands where you left.
/// </summary>
/// <remarks>
/// <para>
/// The headline feature of the rewrite, and until now covered only by the
/// navigation stack's unit tests — which prove the right page comes back, not
/// that it comes back in the right place. Scroll restoration is where the
/// difficulty actually is: a virtualizing panel unrealizes its containers on
/// detach and reports an extent barely larger than the viewport, so an offset
/// set at the wrong moment is silently clamped to zero.
/// </para>
/// <para>
/// These drive a real ListBox with real virtualization, which is the only place
/// that behaviour exists.
/// </para>
/// </remarks>
public sealed class ScrollRestorationTests
{
    private const int ItemHeight = 24;

    private static (Window Window, ListBox List, FakePage Page) Build(int count = 2000)
    {
        var page = new FakePage();

        var list = new ListBox
        {
            ItemsSource = Enumerable.Range(0, count)
                .Select(i => new Row($"id-{i}", $"Item {i}"))
                .ToList(),

            ItemTemplate = new FuncDataTemplate<Row>((row, _) =>
                new TextBlock { Text = row?.Name, Height = ItemHeight }, supportsRecycling: true),
        };

        ScrollAnchor.SetKey(list, "test-list");
        ScrollAnchor.SetColumns(list, 1);
        ScrollAnchor.SetPage(list, page);

        var window = new Window { Width = 600, Height = 400, Content = list };

        window.Show();
        Settle(window);

        return (window, list, page);
    }

    /// <summary>
    /// Lets layout and the posted restore work actually run.
    /// </summary>
    /// <remarks>
    /// Restoration is deliberately spread across dispatcher passes: it scrolls
    /// coarsely by index, then refines pixels only once the extent has grown
    /// enough for the offset to survive. A test that checks immediately measures
    /// the first tier and misses the point.
    /// </remarks>
    private static void Settle(Window window)
    {
        for (var i = 0; i < 12; i++)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void CapturesWhereTheListWasLeft()
    {
        var (window, list, page) = Build();

        list.Scroll!.Offset = new Avalonia.Vector(0, 400 * ItemHeight);
        Settle(window);

        ScrollAnchor.Capture(list, page);

        var saved = Assert.Contains("test-list", page.ScrollPositions);

        Assert.True(saved.OffsetY > 0, "Nothing was captured.");
        Assert.True(saved.FirstIndex > 0, "The first visible item was not identified.");
        Assert.NotNull(saved.FirstItemKey);

        window.Close();
    }

    [AvaloniaFact]
    public void ComesBackToTheSamePlace()
    {
        var (window, list, page) = Build();

        var target = 600 * ItemHeight;

        list.Scroll!.Offset = new Avalonia.Vector(0, target);
        Settle(window);

        ScrollAnchor.Capture(list, page);

        // Back to the top, as an unrealized panel would be on re-entry.
        list.Scroll.Offset = new Avalonia.Vector(0, 0);
        Settle(window);

        ScrollAnchor.Restore(list, page);
        Settle(window);

        // Within a couple of rows: the coarse tier lands on an item boundary and
        // the pixel tier refines from there, so demanding the exact pixel would
        // be testing the arithmetic rather than the behaviour.
        Assert.InRange(list.Scroll.Offset.Y, target - ItemHeight * 3, target + ItemHeight * 3);

        window.Close();
    }

    [AvaloniaFact]
    public void FollowsTheItemWhenTheCollectionShiftedWhileAway()
    {
        var (window, list, page) = Build();

        list.Scroll!.Offset = new Avalonia.Vector(0, 500 * ItemHeight);
        Settle(window);

        ScrollAnchor.Capture(list, page);

        var anchorKey = page.ScrollPositions["test-list"].FirstItemKey;
        Assert.NotNull(anchorKey);

        // A sync lands while the page is away and everything shifts down by ten.
        var grown = Enumerable.Range(0, 10).Select(i => new Row($"new-{i}", $"New {i}"))
            .Concat(((List<Row>)list.ItemsSource!)!)
            .ToList();

        list.ItemsSource = grown;
        list.Scroll.Offset = new Avalonia.Vector(0, 0);
        Settle(window);

        ScrollAnchor.Restore(list, page);
        Settle(window);

        // The anchor item, not the saved pixels: its index moved by ten, so the
        // old offset now points at something else entirely.
        var expected = grown.FindIndex(r => r.Id == anchorKey) * ItemHeight;

        Assert.InRange(list.Scroll.Offset.Y, expected - ItemHeight * 3, expected + ItemHeight * 3);

        window.Close();
    }

    [AvaloniaFact]
    public void DoesNothingWhenThereIsNothingSaved()
    {
        var (window, list, page) = Build();

        // Restoring without a capture must leave the list alone rather than
        // scrolling it somewhere arbitrary.
        ScrollAnchor.Restore(list, page);
        Settle(window);

        Assert.Equal(0, list.Scroll!.Offset.Y);

        window.Close();
    }
}
