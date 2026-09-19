using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace XtreamPlayer.App.Media;

/// <summary>
/// An image that loads from a URL without blocking the UI, and gives up the
/// moment its tile scrolls away.
/// </summary>
/// <remarks>
/// <para>
/// The cancellation behaviour is the important part. A virtualized grid
/// realizes and unrealizes containers constantly during a fast scroll; without
/// cancelling on unrealize, every tile the user flies past leaves a decode
/// queued, and the backlog stutters the UI in a way that looks like broken
/// virtualization.
/// </para>
/// <para>
/// A cache hit is applied synchronously so returning to a previously seen row
/// shows no flicker at all, which is most of what makes scrolling back feel
/// instant.
/// </para>
/// </remarks>
public sealed class AsyncImage : Control
{
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<AsyncImage, string?>(nameof(Source));

    public static readonly StyledProperty<int> DecodeWidthProperty =
        AvaloniaProperty.Register<AsyncImage, int>(nameof(DecodeWidth), 200);

    public static readonly StyledProperty<IBrush?> PlaceholderProperty =
        AvaloniaProperty.Register<AsyncImage, IBrush?>(nameof(Placeholder));

    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<AsyncImage, Stretch>(nameof(Stretch), Stretch.UniformToFill);

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<AsyncImage, CornerRadius>(nameof(CornerRadius));

    /// <summary>Set once at startup; this control is created thousands of times.</summary>
    public static IPosterCache? Cache { get; set; }

    private CancellationTokenSource? _loading;
    private Bitmap? _bitmap;
    private double _opacity;

    static AsyncImage()
    {
        AffectsRender<AsyncImage>(PlaceholderProperty, StretchProperty, CornerRadiusProperty);

        SourceProperty.Changed.AddClassHandler<AsyncImage>((image, _) => image.BeginLoad());
    }

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public int DecodeWidth
    {
        get => GetValue(DecodeWidthProperty);
        set => SetValue(DecodeWidthProperty, value);
    }

    public IBrush? Placeholder
    {
        get => GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Only start if there is nothing to show and nothing already running.
        // Restarting unconditionally cancels the load the property-changed
        // handler just began, which is how a request ends up sent and then
        // immediately abandoned.
        if (_bitmap is null && _loading is null)
        {
            BeginLoad();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        // The tile scrolled away: stop waiting on the decode rather than let a
        // backlog build. The shared fetch itself keeps going, so the next tile
        // asking for the same poster gets it for free.
        CancelLoad();

        // The bitmap itself belongs to the cache and is deliberately not
        // disposed here; only the reference is dropped.
        _bitmap = null;
        _opacity = 0;
    }

    private void CancelLoad()
    {
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = null;
    }

    private void BeginLoad()
    {
        CancelLoad();

        var url = Source;
        _bitmap = null;
        _opacity = 0;

        if (string.IsNullOrWhiteSpace(url) || Cache is null)
        {
            InvalidateVisual();
            return;
        }

        // A cache hit paints immediately and skips the fade, so scrolling back
        // over seen rows has no flicker.
        if (Cache.TryGetCached(url, out var cached) && cached is not null)
        {
            _bitmap = cached;
            _opacity = 1;
            InvalidateVisual();
            return;
        }

        var cts = new CancellationTokenSource();
        _loading = cts;

        _ = LoadAsync(url, cts.Token);
    }

    private async Task LoadAsync(string url, CancellationToken ct)
    {
        var bitmap = await Cache!.GetAsync(url, DecodeWidth, ct).ConfigureAwait(false);

        if (ct.IsCancellationRequested || bitmap is null)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Source may have changed while the decode was in flight.
            if (ct.IsCancellationRequested || !string.Equals(Source, url, StringComparison.Ordinal))
            {
                return;
            }

            _bitmap = bitmap;
            FadeIn();
        });
    }

    /// <summary>
    /// A short fade, driven by render passes rather than an animation object.
    /// </summary>
    /// <remarks>
    /// Thousands of these exist in a grid; allocating a transition per tile is
    /// measurably worse than incrementing a double in Render.
    /// </remarks>
    private void FadeIn()
    {
        _opacity = 0;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        using var clip = CornerRadius != default
            ? context.PushClip(new RoundedRect(bounds, CornerRadius))
            : default;

        if (Placeholder is not null && _opacity < 1)
        {
            context.FillRectangle(Placeholder, bounds);
        }

        if (_bitmap is null)
        {
            return;
        }

        var source = new Rect(_bitmap.Size);
        var destination = Stretch == Stretch.UniformToFill
            ? CropToFill(source, bounds)
            : source;

        if (_opacity < 1)
        {
            _opacity = Math.Min(1, _opacity + 0.16);

            using (context.PushOpacity(_opacity))
            {
                context.DrawImage(_bitmap, destination, bounds);
            }

            // Keep stepping until fully opaque.
            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
            return;
        }

        context.DrawImage(_bitmap, destination, bounds);
    }

    /// <summary>
    /// Centre-crops the source so the image fills the tile without distortion.
    /// </summary>
    private static Rect CropToFill(Rect source, Rect target)
    {
        if (source.Width <= 0 || source.Height <= 0 || target.Height <= 0)
        {
            return source;
        }

        var sourceRatio = source.Width / source.Height;
        var targetRatio = target.Width / target.Height;

        if (Math.Abs(sourceRatio - targetRatio) < 0.001)
        {
            return source;
        }

        if (sourceRatio > targetRatio)
        {
            // Wider than the tile: trim the sides.
            var width = source.Height * targetRatio;
            return new Rect((source.Width - width) / 2, 0, width, source.Height);
        }

        // Taller than the tile: trim top and bottom.
        var height = source.Width / targetRatio;
        return new Rect(0, (source.Height - height) / 2, source.Width, height);
    }
}
