using CommunityToolkit.Mvvm.ComponentModel;

namespace XtreamPlayer.Core.Navigation;

/// <summary>
/// Base for anything the navigation stack can host.
/// </summary>
public abstract partial class PageViewModelBase : ObservableObject, IDisposable
{
    private bool _disposed;

    [ObservableProperty]
    private bool _isBusy;

    public abstract PageKey Key { get; }

    /// <summary>Shown in the breadcrumb bar and the window title.</summary>
    public abstract string Title { get; }

    /// <summary>
    /// Cached by default: keeping the instance alive is what makes Back instant
    /// and is why state survives without being serialized.
    /// </summary>
    public virtual NavigationCachePolicy CachePolicy => NavigationCachePolicy.Cached;

    /// <summary>
    /// Per-list scroll positions, keyed by the ScrollAnchor key in the view.
    /// Lives on the view model so it survives the view being unrealized.
    /// </summary>
    public Dictionary<string, ScrollState> ScrollPositions { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Raised when the page wants its views to restore their scroll positions.
    /// </summary>
    public event EventHandler? ScrollRestoreRequested;

    /// <summary>
    /// Called on every arrival.
    /// </summary>
    /// <param name="parameter">The route parameter.</param>
    /// <param name="isNewInstance">
    /// False when returning to a cached page. Loading work belongs behind this
    /// check — re-running it is exactly the cost the cached stack exists to
    /// avoid.
    /// </param>
    public virtual Task OnNavigatedToAsync(
        object? parameter, bool isNewInstance, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Called before leaving. Scroll capture happens here, while the view is
    /// still realized and its offsets are still meaningful.
    /// </summary>
    public virtual Task OnNavigatingFromAsync(NavigationDirection direction) => Task.CompletedTask;

    /// <summary>
    /// Called after returning to a cached page, once its view is attached.
    /// </summary>
    public virtual Task OnRestoredAsync()
    {
        RequestScrollRestore();
        return Task.CompletedTask;
    }

    protected void RequestScrollRestore() => ScrollRestoreRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// A small snapshot for session persistence and deep links.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT used for ordinary Back navigation, which keeps the live
    /// instance instead. Serializing state per page is boilerplate that drifts,
    /// and it cannot capture things like a loaded detail cache anyway.
    /// </remarks>
    public virtual IReadOnlyDictionary<string, string>? CaptureState() => null;

    public virtual void RestoreState(IReadOnlyDictionary<string, string> state)
    {
    }

    protected virtual void Dispose(bool disposing)
    {
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
