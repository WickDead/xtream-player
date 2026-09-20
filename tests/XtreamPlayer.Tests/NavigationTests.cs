using XtreamPlayer.Core.Navigation;
using Xunit;

namespace XtreamPlayer.Tests;

internal sealed class TestPage(PageKey key, object? parameter = null) : PageViewModelBase
{
    public override PageKey Key { get; } = key;

    public override string Title => Parameter is null ? Key.ToString() : $"{Key} {Parameter}";

    public object? Parameter { get; } = parameter;

    public override NavigationCachePolicy CachePolicy { get; } =
        key == PageKey.Player ? NavigationCachePolicy.Disposable : NavigationCachePolicy.Cached;

    public int EnteredCount { get; private set; }
    public int NewInstanceCount { get; private set; }
    public int LeftCount { get; private set; }
    public int RestoredCount { get; private set; }
    public int DisposeCount { get; private set; }
    public NavigationDirection? LastLeaveDirection { get; private set; }

    /// <summary>Stands in for filter text, selection, and anything else a page holds.</summary>
    public string State { get; set; } = string.Empty;

    public override Task OnNavigatedToAsync(object? parameter, bool isNewInstance, CancellationToken ct = default)
    {
        EnteredCount++;
        if (isNewInstance)
        {
            NewInstanceCount++;
        }

        return Task.CompletedTask;
    }

    public override Task OnNavigatingFromAsync(NavigationDirection direction)
    {
        LeftCount++;
        LastLeaveDirection = direction;
        return Task.CompletedTask;
    }

    public override Task OnRestoredAsync()
    {
        RestoredCount++;
        return base.OnRestoredAsync();
    }

    protected override void Dispose(bool disposing) => DisposeCount++;
}

public sealed class NavigationServiceTests
{
    private readonly Dictionary<NavRoute, TestPage> _created = [];

    private NavigationService Create()
    {
        return new NavigationService(route =>
        {
            var page = new TestPage(route.Key, route.Parameter);
            _created[route] = page;
            return page;
        });
    }

    private TestPage Page(PageKey key, object? parameter = null) =>
        _created[new NavRoute(key, parameter)];

    [Fact]
    public async Task StartsEmpty()
    {
        var nav = Create();

        Assert.Null(nav.Current);
        Assert.False(nav.CanGoBack);
        Assert.False(nav.CanGoForward);
    }

    [Fact]
    public async Task PushingBuildsAStack()
    {
        var nav = Create();

        await nav.NavigateAsync(new NavRoute(PageKey.Dashboard));
        Assert.False(nav.CanGoBack);

        await nav.NavigateAsync(new NavRoute(PageKey.Vod));
        Assert.True(nav.CanGoBack);
        Assert.Equal(PageKey.Vod, nav.Current!.Key);
    }

    /// <summary>
    /// The whole point. Back must return the same live instance, with whatever
    /// the user had typed, selected and scrolled to still intact.
    /// </summary>
    [Fact]
    public async Task BackReturnsTheSameInstanceWithItsStateIntact()
    {
        var nav = Create();

        await nav.NavigateAsync(new NavRoute(PageKey.Vod));
        var vod = (TestPage)nav.Current!;
        vod.State = "category=Action;filter=matrix;selected=1234";
        vod.ScrollPositions["grid"] = new ScrollState(8400, 5000, "item-5000", 6);

        await nav.NavigateAsync(new NavRoute(PageKey.VodDetail, "1234"));
        await nav.GoBackAsync();

        Assert.Same(vod, nav.Current);
        Assert.Equal("category=Action;filter=matrix;selected=1234", vod.State);
        Assert.Equal(8400, vod.ScrollPositions["grid"].OffsetY);

        // Re-entered, but NOT as a new instance: loading work must not re-run.
        Assert.Equal(2, vod.EnteredCount);
        Assert.Equal(1, vod.NewInstanceCount);
        Assert.Equal(1, vod.RestoredCount);
    }

    [Fact]
    public async Task ForwardWorksAfterBack()
    {
        var nav = Create();

        await nav.NavigateAsync(new NavRoute(PageKey.Vod));
        await nav.NavigateAsync(new NavRoute(PageKey.VodDetail, "1"));
        await nav.GoBackAsync();

        Assert.True(nav.CanGoForward);

        await nav.GoForwardAsync();

        Assert.Equal(PageKey.VodDetail, nav.Current!.Key);
        Assert.False(nav.CanGoForward);
    }

    /// <summary>Browser semantics: a new push abandons the forward history.</summary>
    [Fact]
    public async Task PushingClearsTheForwardStack()
    {
        var nav = Create();

        await nav.NavigateAsync(new NavRoute(PageKey.Vod));
        await nav.NavigateAsync(new NavRoute(PageKey.VodDetail, "1"));
        await nav.GoBackAsync();
        await nav.NavigateAsync(new NavRoute(PageKey.Series));

        Assert.False(nav.CanGoForward);
    }

    /// <summary>
    /// Without this, a double-click on a poster pushes the detail page twice and
    /// Back appears not to work.
    /// </summary>
    [Fact]
    public async Task NavigatingToTheCurrentDestinationIsIgnored()
    {
        var nav = Create();

        await nav.NavigateAsync(new NavRoute(PageKey.VodDetail, "1234"));
        var first = nav.Current;

        await nav.NavigateAsync(new NavRoute(PageKey.VodDetail, "1234"));

        Assert.Same(first, nav.Current);
        Assert.False(nav.CanGoBack);
    }

    [Fact]
    public async Task TheSamePageWithADifferentParameterIsADifferentDestination()
    {
        var nav = Create();

        await nav.NavigateAsync(new NavRoute(PageKey.VodDetail, "1"));
        await nav.NavigateAsync(new NavRoute(PageKey.VodDetail, "2"));

        Assert.True(nav.CanGoBack);
        Assert.Equal("2", ((TestPage)nav.Current!).Parameter);
    }

    [Fact]
    public async Task BreadcrumbsTrackTheStack()
    {
        var nav = Create();

        await nav.NavigateAsync(new NavRoute(PageKey.Dashboard));
        await nav.NavigateAsync(new NavRoute(PageKey.Series));
        await nav.NavigateAsync(new NavRoute(PageKey.SeriesDetail, "696"));

        Assert.Equal(
            ["Dashboard", "Series", "SeriesDetail 696"],
            nav.Breadcrumbs.Select(c => c.Label));
    }

    [Fact]
    public async Task PoppingToABreadcrumbDropsEverythingAbove()
    {
        var nav = Create();

        await nav.NavigateAsync(new NavRoute(PageKey.Dashboard));
        await nav.NavigateAsync(new NavRoute(PageKey.Series));
        await nav.NavigateAsync(new NavRoute(PageKey.SeriesDetail, "696"));
        await nav.NavigateAsync(new NavRoute(PageKey.Player, "ep-1"));

        await nav.PopToIndexAsync(0);

        Assert.Equal(PageKey.Dashboard, nav.Current!.Key);
        Assert.False(nav.CanGoBack);
        Assert.False(nav.CanGoForward);
        Assert.Single(nav.Breadcrumbs);
    }

    [Fact]
    public async Task ReplaceSwapsWithoutGrowingTheStack()
    {
        var nav = Create();

        await nav.NavigateAsync(new NavRoute(PageKey.Dashboard));
        await nav.NavigateAsync(new NavRoute(PageKey.Vod));
        await nav.ReplaceAsync(new NavRoute(PageKey.Series));

        Assert.Equal(PageKey.Series, nav.Current!.Key);
        Assert.Equal(2, nav.Breadcrumbs.Count);

        await nav.GoBackAsync();
        Assert.Equal(PageKey.Dashboard, nav.Current!.Key);
    }

    /// <summary>
    /// A profile switch invalidates every cached page, because content ids are
    /// not comparable across providers.
    /// </summary>
    [Fact]
    public async Task ResetDiscardsTheEntireStack()
    {
        var nav = Create();

        await nav.NavigateAsync(new NavRoute(PageKey.Dashboard));
        await nav.NavigateAsync(new NavRoute(PageKey.Vod));
        await nav.NavigateAsync(new NavRoute(PageKey.VodDetail, "1"));

        await nav.ResetToAsync(new NavRoute(PageKey.Dashboard));

        Assert.False(nav.CanGoBack);
        Assert.False(nav.CanGoForward);
        Assert.Single(nav.Breadcrumbs);
    }

    // ---- disposal -------------------------------------------------------

    /// <summary>
    /// The player holds a media session. Leaving it must stop and release that,
    /// not leave it running behind a cached page.
    /// </summary>
    [Fact]
    public async Task PoppingDisposesTransientPagesExactlyOnce()
    {
        var nav = Create();

        await nav.NavigateAsync(new NavRoute(PageKey.SeriesDetail, "696"));
        await nav.NavigateAsync(new NavRoute(PageKey.Player, "ep-1"));

        var player = Page(PageKey.Player, "ep-1");
        Assert.Equal(0, player.DisposeCount);

        await nav.GoBackAsync();

        // Back keeps it on the forward stack, so it is not disposed yet.
        Assert.Equal(0, player.DisposeCount);

        // A new push abandons forward history, which is when it goes.
        await nav.NavigateAsync(new NavRoute(PageKey.Vod));
        Assert.Equal(1, player.DisposeCount);
    }

    [Fact]
    public async Task CachedPagesAreNeverDisposedWhilstOnTheStack()
    {
        var nav = Create();

        await nav.NavigateAsync(new NavRoute(PageKey.Vod));
        var vod = Page(PageKey.Vod);

        await nav.NavigateAsync(new NavRoute(PageKey.VodDetail, "1"));
        await nav.GoBackAsync();
        await nav.NavigateAsync(new NavRoute(PageKey.Series));

        Assert.Equal(0, vod.DisposeCount);
    }

    [Fact]
    public async Task ResetDisposesTransientPages()
    {
        var nav = Create();

        await nav.NavigateAsync(new NavRoute(PageKey.Live));
        await nav.NavigateAsync(new NavRoute(PageKey.Player, "604"));

        await nav.ResetToAsync(new NavRoute(PageKey.Dashboard));

        Assert.Equal(1, Page(PageKey.Player, "604").DisposeCount);
    }

    /// <summary>
    /// Without a cap, a long browsing session accumulates view models forever.
    /// </summary>
    [Fact]
    public async Task TheStackIsCappedAndDropsTheOldestEntries()
    {
        var nav = Create();

        for (var i = 0; i < 30; i++)
        {
            await nav.NavigateAsync(new NavRoute(PageKey.VodDetail, i.ToString()));
        }

        Assert.True(nav.Breadcrumbs.Count <= 21,
            $"Expected the stack to be capped, but it holds {nav.Breadcrumbs.Count}.");

        // The most recent entries are the ones kept.
        Assert.Equal("29", ((TestPage)nav.Current!).Parameter);
    }

    // ---- lifecycle ordering ---------------------------------------------

    [Fact]
    public async Task LeavingReportsTheDirection()
    {
        var nav = Create();

        await nav.NavigateAsync(new NavRoute(PageKey.Vod));
        var vod = Page(PageKey.Vod);

        await nav.NavigateAsync(new NavRoute(PageKey.VodDetail, "1"));
        Assert.Equal(NavigationDirection.Forward, vod.LastLeaveDirection);

        await nav.GoBackAsync();
        Assert.Equal(NavigationDirection.Back, Page(PageKey.VodDetail, "1").LastLeaveDirection);
    }

    [Fact]
    public async Task RestoreIsOnlyRaisedWhenReturningToACachedPage()
    {
        var nav = Create();

        await nav.NavigateAsync(new NavRoute(PageKey.Vod));
        var vod = Page(PageKey.Vod);

        Assert.Equal(0, vod.RestoredCount);

        await nav.NavigateAsync(new NavRoute(PageKey.VodDetail, "1"));
        await nav.GoBackAsync();

        Assert.Equal(1, vod.RestoredCount);
    }

    [Fact]
    public async Task NavigatedReportsWhetherThePageIsNew()
    {
        var nav = Create();
        var events = new List<NavigatedEventArgs>();
        nav.Navigated += (_, e) => events.Add(e);

        await nav.NavigateAsync(new NavRoute(PageKey.Vod));
        await nav.NavigateAsync(new NavRoute(PageKey.VodDetail, "1"));
        await nav.GoBackAsync();

        Assert.Equal(3, events.Count);
        Assert.True(events[0].IsNewInstance);
        Assert.True(events[1].IsNewInstance);
        Assert.False(events[2].IsNewInstance);
        Assert.Equal(NavigationDirection.Back, events[2].Direction);
    }

    [Fact]
    public async Task BackAndForwardAtTheEdgesDoNothing()
    {
        var nav = Create();

        await nav.GoBackAsync();
        await nav.GoForwardAsync();
        Assert.Null(nav.Current);

        await nav.NavigateAsync(new NavRoute(PageKey.Dashboard));
        await nav.GoBackAsync();

        Assert.Equal(PageKey.Dashboard, nav.Current!.Key);
    }

    [Fact]
    public async Task PopToAnInvalidIndexDoesNothing()
    {
        var nav = Create();

        await nav.NavigateAsync(new NavRoute(PageKey.Dashboard));
        await nav.NavigateAsync(new NavRoute(PageKey.Vod));

        await nav.PopToIndexAsync(-1);
        await nav.PopToIndexAsync(99);

        Assert.Equal(PageKey.Vod, nav.Current!.Key);
    }
}

public sealed class NavRouteTests
{
    [Fact]
    public void RoutesMatchOnPageAndParameter()
    {
        Assert.True(new NavRoute(PageKey.Vod).SameDestinationAs(new NavRoute(PageKey.Vod)));
        Assert.True(new NavRoute(PageKey.VodDetail, "1")
            .SameDestinationAs(new NavRoute(PageKey.VodDetail, "1")));

        Assert.False(new NavRoute(PageKey.VodDetail, "1")
            .SameDestinationAs(new NavRoute(PageKey.VodDetail, "2")));
        Assert.False(new NavRoute(PageKey.Vod).SameDestinationAs(new NavRoute(PageKey.Series)));
        Assert.False(new NavRoute(PageKey.Vod).SameDestinationAs(null));
    }

    [Fact]
    public void ScrollStateKnowsWhenItIsWorthRestoring()
    {
        Assert.False(ScrollState.Empty.IsMeaningful);
        Assert.False(new ScrollState(0, 0, "x", 6).IsMeaningful);
        Assert.True(new ScrollState(8400, 5000, "x", 6).IsMeaningful);
        Assert.True(new ScrollState(0, 12, "x", 1).IsMeaningful);
    }
}
