using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace XtreamPlayer.Core.Navigation;

public interface INavigationService
{
    PageViewModelBase? Current { get; }
    bool CanGoBack { get; }
    bool CanGoForward { get; }
    IReadOnlyList<NavCrumb> Breadcrumbs { get; }

    /// <summary>Pushes a new page. Clears the forward stack, as a browser does.</summary>
    Task NavigateAsync(NavRoute route, CancellationToken ct = default);

    Task GoBackAsync(CancellationToken ct = default);
    Task GoForwardAsync(CancellationToken ct = default);

    /// <summary>Pops back to a breadcrumb.</summary>
    Task PopToIndexAsync(int index, CancellationToken ct = default);

    /// <summary>Swaps the current page without growing the stack.</summary>
    Task ReplaceAsync(NavRoute route, CancellationToken ct = default);

    /// <summary>Discards everything and starts again. Used on a profile switch.</summary>
    Task ResetToAsync(NavRoute route, CancellationToken ct = default);

    event EventHandler<NavigatedEventArgs>? Navigated;
}

internal sealed class NavigationEntry(NavRoute route, PageViewModelBase viewModel)
{
    public NavRoute Route { get; } = route;
    public PageViewModelBase ViewModel { get; } = viewModel;
}

/// <summary>
/// A real back stack.
/// </summary>
/// <remarks>
/// <para>
/// The previous app had no navigation at all: a switch on a string that
/// unmounted the previous page, destroying its filter text, selected category,
/// selection and scroll position every single time. This exists to make Back
/// return you to exactly where you were.
/// </para>
/// <para>
/// View model instances are cached on the stack rather than rebuilt from
/// serialized state. With catalogs of 68,595 items, rebuilding means re-running
/// the category projection, hidden-category filter, favorites join, text filter
/// and sort on every Back — which is the latency that made the old app feel
/// wrong. Keeping the instance also makes scroll restoration tractable, because
/// the collection is identical on return: same count, same order, same object
/// identities.
/// </para>
/// </remarks>
public sealed partial class NavigationService : ObservableObject, INavigationService
{
    /// <summary>
    /// Depth cap. Without one, a long browsing session accumulates view models
    /// holding collection plumbing forever.
    /// </summary>
    private const int MaxDepth = 20;

    private readonly List<NavigationEntry> _back = [];
    private readonly List<NavigationEntry> _forward = [];
    private readonly Func<NavRoute, PageViewModelBase> _factory;
    private readonly ObservableCollection<NavCrumb> _breadcrumbs = [];

    private NavigationEntry? _current;

    public NavigationService(Func<NavRoute, PageViewModelBase> factory) => _factory = factory;

    public PageViewModelBase? Current => _current?.ViewModel;

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    public IReadOnlyList<NavCrumb> Breadcrumbs => _breadcrumbs;

    public event EventHandler<NavigatedEventArgs>? Navigated;

    public async Task NavigateAsync(NavRoute route, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(route);

        // Suppress a push to where we already are. Without this a double-click
        // on a poster pushes the detail page twice and Back appears broken.
        if (_current is not null && route.SameDestinationAs(_current.Route))
        {
            return;
        }

        await LeaveCurrentAsync(NavigationDirection.Forward).ConfigureAwait(false);

        if (_current is not null)
        {
            _back.Add(_current);
            TrimToDepth();
        }

        // A new push invalidates any forward history, exactly like a browser.
        DisposeAll(_forward);
        _forward.Clear();

        var entry = new NavigationEntry(route, _factory(route));
        _current = entry;

        await EnterAsync(entry, isNewInstance: true, NavigationDirection.Forward, ct)
            .ConfigureAwait(false);
    }

    public async Task GoBackAsync(CancellationToken ct = default)
    {
        if (_back.Count == 0)
        {
            return;
        }

        await LeaveCurrentAsync(NavigationDirection.Back).ConfigureAwait(false);

        if (_current is not null)
        {
            // Back preserves forward history so Alt+Right works.
            _forward.Insert(0, _current);
        }

        var entry = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        _current = entry;

        await EnterAsync(entry, isNewInstance: false, NavigationDirection.Back, ct)
            .ConfigureAwait(false);
    }

    public async Task GoForwardAsync(CancellationToken ct = default)
    {
        if (_forward.Count == 0)
        {
            return;
        }

        await LeaveCurrentAsync(NavigationDirection.Forward).ConfigureAwait(false);

        if (_current is not null)
        {
            _back.Add(_current);
        }

        var entry = _forward[0];
        _forward.RemoveAt(0);
        _current = entry;

        await EnterAsync(entry, isNewInstance: false, NavigationDirection.Forward, ct)
            .ConfigureAwait(false);
    }

    public async Task PopToIndexAsync(int index, CancellationToken ct = default)
    {
        if (index < 0 || index >= _back.Count)
        {
            return;
        }

        await LeaveCurrentAsync(NavigationDirection.Back).ConfigureAwait(false);

        // Everything above the target is gone for good, including the page we
        // were on; disposable entries must be disposed rather than dropped.
        if (_current is not null)
        {
            DisposeIfTransient(_current);
        }

        for (var i = _back.Count - 1; i > index; i--)
        {
            DisposeIfTransient(_back[i]);
            _back.RemoveAt(i);
        }

        DisposeAll(_forward);
        _forward.Clear();

        var entry = _back[index];
        _back.RemoveAt(index);
        _current = entry;

        await EnterAsync(entry, isNewInstance: false, NavigationDirection.Back, ct)
            .ConfigureAwait(false);
    }

    public async Task ReplaceAsync(NavRoute route, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(route);

        await LeaveCurrentAsync(NavigationDirection.Replace).ConfigureAwait(false);

        if (_current is not null)
        {
            DisposeIfTransient(_current);
        }

        var entry = new NavigationEntry(route, _factory(route));
        _current = entry;

        await EnterAsync(entry, isNewInstance: true, NavigationDirection.Replace, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Clears the whole stack.
    /// </summary>
    /// <remarks>
    /// Used when the active profile changes. Every cached page holds items from
    /// the old provider's catalog, and content ids are not comparable across
    /// providers, so none of it can be reused. The old app's equivalent cleared
    /// the catalogs but left the current view pointing at a now-meaningless page.
    /// </remarks>
    public async Task ResetToAsync(NavRoute route, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(route);

        await LeaveCurrentAsync(NavigationDirection.Replace).ConfigureAwait(false);

        if (_current is not null)
        {
            DisposeIfTransient(_current);
        }

        DisposeAll(_back);
        DisposeAll(_forward);
        _back.Clear();
        _forward.Clear();

        var entry = new NavigationEntry(route, _factory(route));
        _current = entry;

        await EnterAsync(entry, isNewInstance: true, NavigationDirection.Replace, ct)
            .ConfigureAwait(false);
    }

    // ---- internals ------------------------------------------------------

    private async Task LeaveCurrentAsync(NavigationDirection direction)
    {
        if (_current is null)
        {
            return;
        }

        // Scroll capture happens here, while the view is still realized.
        await _current.ViewModel.OnNavigatingFromAsync(direction).ConfigureAwait(false);
    }

    private async Task EnterAsync(
        NavigationEntry entry, bool isNewInstance, NavigationDirection direction, CancellationToken ct)
    {
        await entry.ViewModel.OnNavigatedToAsync(entry.Route.Parameter, isNewInstance, ct)
            .ConfigureAwait(false);

        if (!isNewInstance)
        {
            await entry.ViewModel.OnRestoredAsync().ConfigureAwait(false);
        }

        RebuildBreadcrumbs();

        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));

        Navigated?.Invoke(this, new NavigatedEventArgs(entry.Route, direction, isNewInstance));
    }

    private void RebuildBreadcrumbs()
    {
        _breadcrumbs.Clear();

        for (var i = 0; i < _back.Count; i++)
        {
            _breadcrumbs.Add(new NavCrumb(i, _back[i].ViewModel.Title, _back[i].Route));
        }

        if (_current is not null)
        {
            _breadcrumbs.Add(new NavCrumb(_back.Count, _current.ViewModel.Title, _current.Route));
        }
    }

    /// <summary>
    /// Drops the oldest entries once the stack is too deep.
    /// </summary>
    private void TrimToDepth()
    {
        while (_back.Count > MaxDepth)
        {
            DisposeIfTransient(_back[0]);
            _back.RemoveAt(0);
        }
    }

    private static void DisposeIfTransient(NavigationEntry entry)
    {
        // Cached pages are kept alive deliberately; only transient ones (the
        // player, which holds a media session) are disposed.
        if (entry.ViewModel.CachePolicy == NavigationCachePolicy.Disposable)
        {
            entry.ViewModel.Dispose();
        }
    }

    private static void DisposeAll(List<NavigationEntry> entries)
    {
        foreach (var entry in entries)
        {
            DisposeIfTransient(entry);
        }
    }
}
