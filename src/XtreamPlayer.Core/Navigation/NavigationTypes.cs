namespace XtreamPlayer.Core.Navigation;

public enum PageKey
{
    Dashboard,
    Providers,
    Live,
    Guide,
    Vod,
    VodDetail,
    Series,
    SeriesDetail,
    Player,
    Downloads,
    Settings,
}

/// <summary>A destination: which page, plus whatever it needs to identify itself.</summary>
public sealed record NavRoute(PageKey Key, object? Parameter = null)
{
    /// <summary>
    /// Two routes are the same destination when the page and parameter match.
    /// Used to suppress duplicate pushes.
    /// </summary>
    public bool SameDestinationAs(NavRoute? other) =>
        other is not null && other.Key == Key && Equals(other.Parameter, Parameter);

    public override string ToString() =>
        Parameter is null ? Key.ToString() : $"{Key}({Parameter})";
}

public enum NavigationCachePolicy
{
    /// <summary>Kept alive on the stack so returning is instant and state survives.</summary>
    Cached,

    /// <summary>Disposed when popped. The player holds a media session; it must not linger.</summary>
    Disposable,
}

public enum NavigationDirection
{
    Forward,
    Back,
    Replace,
}

/// <summary>
/// Everything needed to restore a list's scroll position.
/// </summary>
/// <remarks>
/// Four values, not one. A raw pixel offset alone is wrong whenever the
/// collection changed while the page was away, or the window was resized so the
/// grid has a different column count. See ScrollAnchor for how each is used.
/// </remarks>
public readonly record struct ScrollState(
    double OffsetY,
    int FirstIndex,
    string? FirstItemKey,
    int ColumnCount)
{
    public static ScrollState Empty { get; } = new(0, 0, null, 1);

    public bool IsMeaningful => OffsetY > 0.5 || FirstIndex > 0;
}

public sealed record NavCrumb(int Index, string Label, NavRoute Route);

public sealed class NavigatedEventArgs(
    NavRoute route,
    NavigationDirection direction,
    bool isNewInstance) : EventArgs
{
    public NavRoute Route { get; } = route;
    public NavigationDirection Direction { get; } = direction;

    /// <summary>False when returning to a cached page, which must not reload.</summary>
    public bool IsNewInstance { get; } = isNewInstance;
}
