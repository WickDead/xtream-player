using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using XtreamPlayer.Core.Models;

namespace XtreamPlayer.App.Controls;

/// <summary>One channel row in the guide.</summary>
public sealed record EpgRow(string ChannelId, string ChannelName, IReadOnlyList<EpgProgramme> Programmes);

public sealed class EpgProgrammeEventArgs(EpgRow row, EpgProgramme programme) : EventArgs
{
    public EpgRow Row { get; } = row;
    public EpgProgramme Programme { get; } = programme;
}

/// <summary>
/// The guide grid: channels down, time across.
/// </summary>
/// <remarks>
/// <para>
/// Drawn directly rather than built from controls. A thousand channels over
/// thirty hours is well over 100,000 programme blocks, and one container per
/// block is hopeless no matter how it is virtualized. This renders only the
/// visible time-by-channel rectangle, which is a few hundred blocks regardless
/// of how large the guide is.
/// </para>
/// <para>
/// It implements <see cref="ILogicalScrollable"/> so it works inside a normal
/// ScrollViewer, which keeps scrollbars, wheel handling and touch behaviour
/// standard instead of reimplemented.
/// </para>
/// <para>
/// The one real performance trap here is text. Building a FormattedText for
/// every visible block on every frame stutters badly, so layouts are cached by
/// programme and width.
/// </para>
/// </remarks>
public sealed class EpgTimelineCanvas : Control, ILogicalScrollable
{
    private const double RowHeight = 56;
    private const double BlockGap = 2;
    private const double TextPadding = 8;

    /// <summary>
    /// Below this a block cannot show enough of a title to be worth reading, and
    /// the ellipsised stub of one next to the stub of the next block just reads
    /// as garbled text. Blocks narrower than this are drawn empty; the tooltip
    /// and the detail panel still name them.
    /// </summary>
    private const double MinimumTextWidth = 48;

    /// <summary>Titles, and the smaller size used for times and placeholders.</summary>
    private const double TitleFontSize = 12.5;
    private const double DetailFontSize = 11;

    /// <summary>Bounded so a long session cannot accumulate layouts forever.</summary>
    private const int MaxCachedLayouts = 2000;

    private readonly Dictionary<(string Content, int Width, double FontSize), FormattedText> _textCache = [];
    private readonly DispatcherTimer _nowTimer;

    private IReadOnlyList<EpgRow> _rows = [];
    private Size _extent;
    private Vector _offset;
    private Size _viewport;
    private EventHandler? _scrollInvalidated;

    public EpgTimelineCanvas()
    {
        ClipToBounds = true;
        Focusable = true;

        // The "now" line has to keep moving, but a guide is read for minutes at
        // a time so redrawing twice a minute is plenty.
        _nowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _nowTimer.Tick += (_, _) => InvalidateVisual();
    }

    public static readonly StyledProperty<DateTimeOffset> WindowStartProperty =
        AvaloniaProperty.Register<EpgTimelineCanvas, DateTimeOffset>(nameof(WindowStart));

    public static readonly StyledProperty<double> PixelsPerMinuteProperty =
        AvaloniaProperty.Register<EpgTimelineCanvas, double>(nameof(PixelsPerMinute), 6);

    public static readonly StyledProperty<TimeSpan> WindowLengthProperty =
        AvaloniaProperty.Register<EpgTimelineCanvas, TimeSpan>(
            nameof(WindowLength), TimeSpan.FromHours(30));

    public DateTimeOffset WindowStart
    {
        get => GetValue(WindowStartProperty);
        set => SetValue(WindowStartProperty, value);
    }

    /// <summary>Horizontal zoom. 6 px/min shows about two hours on a wide window.</summary>
    public double PixelsPerMinute
    {
        get => GetValue(PixelsPerMinuteProperty);
        set => SetValue(PixelsPerMinuteProperty, value);
    }

    public TimeSpan WindowLength
    {
        get => GetValue(WindowLengthProperty);
        set => SetValue(WindowLengthProperty, value);
    }

    /// <summary>
    /// A styled property, not a plain CLR one, so the view can bind to it.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<EpgRow>> RowsProperty =
        AvaloniaProperty.Register<EpgTimelineCanvas, IReadOnlyList<EpgRow>>(
            nameof(Rows), []);

    public IReadOnlyList<EpgRow> Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    /// <summary>
    /// Raised when the row set changes, so the pinned channel column repaints.
    /// </summary>
    /// <remarks>
    /// The column follows this canvas rather than scrolling itself, and it used
    /// to repaint only on OffsetChanged. Changing the guide's category replaces
    /// the rows without moving the scroll offset, so the programmes updated
    /// while the channel names beside them stayed on the previous category.
    /// </remarks>
    public event EventHandler? RowsChanged;

    public event EventHandler<EpgProgrammeEventArgs>? ProgrammeSelected;
    public event EventHandler<EpgProgrammeEventArgs>? ProgrammeActivated;

    public EpgProgramme? SelectedProgramme { get; private set; }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _nowTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _nowTimer.Stop();
    }

    static EpgTimelineCanvas()
    {
        AffectsRender<EpgTimelineCanvas>(WindowStartProperty, PixelsPerMinuteProperty);

        RowsProperty.Changed.AddClassHandler<EpgTimelineCanvas>((canvas, args) =>
        {
            canvas._rows = args.NewValue as IReadOnlyList<EpgRow> ?? [];

            // A new row set means every cached text layout belongs to programmes
            // that are no longer on screen.
            canvas._textCache.Clear();
            canvas.UpdateExtent();
            canvas.InvalidateVisual();
            canvas.RowsChanged?.Invoke(canvas, EventArgs.Empty);
        });

        PixelsPerMinuteProperty.Changed.AddClassHandler<EpgTimelineCanvas>((canvas, _) =>
        {
            // Block widths change with zoom, so cached layouts no longer fit.
            canvas._textCache.Clear();
            canvas.UpdateExtent();
        });

        WindowLengthProperty.Changed.AddClassHandler<EpgTimelineCanvas>(
            (canvas, _) => canvas.UpdateExtent());
    }

    private void UpdateExtent()
    {
        _extent = new Size(
            WindowLength.TotalMinutes * PixelsPerMinute,
            Math.Max(1, _rows.Count) * RowHeight);

        // Scrolled to channel 5,000 and then filtered down to a category of 50:
        // without re-clamping, the offset stays past the new extent and the grid
        // renders empty with no way to tell why.
        var clamped = new Vector(
            Math.Clamp(_offset.X, 0, Math.Max(0, _extent.Width - _viewport.Width)),
            Math.Clamp(_offset.Y, 0, Math.Max(0, _extent.Height - _viewport.Height)));

        if (clamped != _offset)
        {
            _offset = clamped;
            OffsetChanged?.Invoke(this, EventArgs.Empty);
        }

        _scrollInvalidated?.Invoke(this, EventArgs.Empty);
    }

    // ---- ILogicalScrollable ---------------------------------------------

    public bool CanHorizontallyScroll { get; set; } = true;
    public bool CanVerticallyScroll { get; set; } = true;
    public bool IsLogicalScrollEnabled => true;

    public Size ScrollSize => new(PixelsPerMinute * 30, RowHeight);

    public Size PageScrollSize => new(_viewport.Width * 0.9, _viewport.Height);

    public Size Extent => _extent;

    public Vector Offset
    {
        get => _offset;
        set
        {
            var clamped = new Vector(
                Math.Clamp(value.X, 0, Math.Max(0, _extent.Width - _viewport.Width)),
                Math.Clamp(value.Y, 0, Math.Max(0, _extent.Height - _viewport.Height)));

            if (clamped == _offset)
            {
                return;
            }

            _offset = clamped;
            OffsetChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }
    }

    public Size Viewport => _viewport;

    /// <summary>Lets the channel column and time ruler follow this canvas.</summary>
    public event EventHandler? OffsetChanged;

    event EventHandler? ILogicalScrollable.ScrollInvalidated
    {
        add => _scrollInvalidated += value;
        remove => _scrollInvalidated -= value;
    }

    public bool BringIntoView(Control target, Rect targetRect) => false;

    public Control? GetControlInDirection(NavigationDirection direction, Control? from) => null;

    public void RaiseScrollInvalidated(EventArgs e) => _scrollInvalidated?.Invoke(this, e);

    protected override Size MeasureOverride(Size availableSize)
    {
        UpdateExtent();
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _viewport = finalSize;
        _scrollInvalidated?.Invoke(this, EventArgs.Empty);
        return finalSize;
    }

    // ---- rendering -------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        if (_rows.Count == 0 || Bounds.Width <= 0)
        {
            return;
        }

        var surface = Brush("SurfaceSunken", Colors.Black);
        var block = Brush("SurfaceRaised", Color.Parse("#1B1B24"));
        var blockNow = Brush("AccentSubtle", Color.Parse("#2A3A5F"));
        var text = Brush("TextPrimary", Colors.White);
        var muted = Brush("TextMuted", Color.Parse("#8087A0"));
        var accent = Brush("Accent", Color.Parse("#5B8CFF"));
        var live = Brush("LiveDot", Color.Parse("#FF5B6E"));

        context.FillRectangle(surface, new Rect(Bounds.Size));

        var now = DateTimeOffset.Now;
        var minutesPerPixel = 1 / PixelsPerMinute;

        // Only the visible rows are considered; everything else costs nothing.
        var firstRow = Math.Max(0, (int)(_offset.Y / RowHeight));
        var lastRow = Math.Min(_rows.Count - 1, (int)((_offset.Y + _viewport.Height) / RowHeight));

        var visibleStart = WindowStart.AddMinutes(_offset.X * minutesPerPixel);
        var visibleEnd = WindowStart.AddMinutes((_offset.X + _viewport.Width) * minutesPerPixel);

        for (var rowIndex = firstRow; rowIndex <= lastRow; rowIndex++)
        {
            var row = _rows[rowIndex];
            var y = rowIndex * RowHeight - _offset.Y;

            // Row separator, drawn once rather than per block.
            context.FillRectangle(muted,
                new Rect(0, y + RowHeight - 1, Bounds.Width, 0.5), 0);

            if (row.Programmes.Count == 0)
            {
                DrawText(context, "No guide data", muted,
                    new Rect(TextPadding, y + 18, Math.Min(240, Bounds.Width), 20), DetailFontSize);
                continue;
            }

            // Binary search to the first programme that could be on screen,
            // rather than scanning a day of listings per row.
            var index = FirstVisibleIndex(row.Programmes, visibleStart);

            for (; index < row.Programmes.Count; index++)
            {
                var programme = row.Programmes[index];

                if (programme.Start >= visibleEnd)
                {
                    break;
                }

                var left = (programme.Start - WindowStart).TotalMinutes * PixelsPerMinute - _offset.X;
                var right = (programme.Stop - WindowStart).TotalMinutes * PixelsPerMinute - _offset.X;

                var width = right - left - BlockGap;
                if (width <= 1)
                {
                    continue;
                }

                var isNow = programme.IsNowPlaying(now);
                var rect = new Rect(left, y + 2, width, RowHeight - 6);

                context.FillRectangle(isNow ? blockNow : block, rect, 4);

                if (isNow)
                {
                    // A thin accent edge reads as "on now" without the fill
                    // fighting the text.
                    context.FillRectangle(accent, new Rect(rect.X, rect.Y, 3, rect.Height), 2);
                }

                if (ReferenceEquals(programme, SelectedProgramme))
                {
                    context.DrawRectangle(new Pen(accent, 1.5), rect, 4);
                }

                // Clip so a long title cannot spill into the next block.
                if (width >= MinimumTextWidth)
                {
                    using (context.PushClip(rect))
                    {
                        DrawText(context, programme.Title, text,
                            new Rect(rect.X + TextPadding, rect.Y + 8,
                                Math.Max(10, width - TextPadding * 2), 18),
                            TitleFontSize);

                        if (rect.Height > 34 && width > 90)
                        {
                            DrawText(context,
                                $"{programme.Start.ToLocalTime():HH:mm} - {programme.Stop.ToLocalTime():HH:mm}",
                                muted,
                                new Rect(rect.X + TextPadding, rect.Y + 28,
                                    Math.Max(10, width - TextPadding * 2), 16),
                                DetailFontSize);
                        }
                    }
                }
            }
        }

        // The now line, last so it sits above every block.
        var nowX = (now - WindowStart).TotalMinutes * PixelsPerMinute - _offset.X;

        if (nowX >= 0 && nowX <= Bounds.Width)
        {
            context.FillRectangle(live, new Rect(nowX - 1, 0, 2, Bounds.Height));
        }
    }

    /// <summary>
    /// Binary search for the first programme still running at a given time.
    /// </summary>
    private static int FirstVisibleIndex(IReadOnlyList<EpgProgramme> programmes, DateTimeOffset at)
    {
        int low = 0, high = programmes.Count - 1, result = programmes.Count;

        while (low <= high)
        {
            var mid = (low + high) / 2;

            if (programmes[mid].Stop > at)
            {
                result = mid;
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }

        return result;
    }

    /// <summary>
    /// Draws text through a bounded cache.
    /// </summary>
    /// <remarks>
    /// Constructing a FormattedText for every visible block on every frame is
    /// the single thing that makes a guide grid stutter. Layouts are keyed by
    /// content and rounded width so a resize or zoom rebuilds them but a scroll
    /// does not.
    /// </remarks>
    private void DrawText(
        DrawingContext context, string content, IBrush brush, Rect bounds, double fontSize)
    {
        if (bounds.Width < 8 || string.IsNullOrEmpty(content))
        {
            return;
        }

        // Font size belongs in the key: the same string is drawn at two sizes on
        // this control, and keying on content and width alone would hand a title
        // the layout built for a timestamp.
        var key = (content, (int)bounds.Width, fontSize);

        if (!_textCache.TryGetValue(key, out var layout))
        {
            if (_textCache.Count > MaxCachedLayouts)
            {
                _textCache.Clear();
            }

            layout = new FormattedText(
                content,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                Typeface.Default,
                fontSize,
                brush)
            {
                MaxTextWidth = bounds.Width,
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };

            _textCache[key] = layout;
        }

        // The brush can differ between a cached layout and this draw (selected
        // versus not), so it is set every time rather than baked into the key.
        layout.SetForegroundBrush(brush);
        context.DrawText(layout, bounds.TopLeft);
    }

    private IBrush Brush(string key, Color fallback) =>
        this.TryFindResource(key, out var value) && value is IBrush found
            ? found
            : new SolidColorBrush(fallback);

    // ---- input -----------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var point = e.GetPosition(this);
        var hit = HitTest(point);

        if (hit is null)
        {
            return;
        }

        SelectedProgramme = hit.Value.Programme;
        InvalidateVisual();

        var args = new EpgProgrammeEventArgs(hit.Value.Row, hit.Value.Programme);
        ProgrammeSelected?.Invoke(this, args);

        if (e.ClickCount >= 2)
        {
            ProgrammeActivated?.Invoke(this, args);
        }

        Focus();
        e.Handled = true;
    }

    private (EpgRow Row, EpgProgramme Programme)? HitTest(Point point)
    {
        var rowIndex = (int)((point.Y + _offset.Y) / RowHeight);

        if (rowIndex < 0 || rowIndex >= _rows.Count)
        {
            return null;
        }

        var row = _rows[rowIndex];
        var at = WindowStart.AddMinutes((point.X + _offset.X) / PixelsPerMinute);

        foreach (var programme in row.Programmes)
        {
            if (programme.Start <= at && programme.Stop > at)
            {
                return (row, programme);
            }
        }

        return null;
    }

    /// <summary>Scrolls so the current time is just inside the left edge.</summary>
    public void ScrollToNow()
    {
        var minutes = (DateTimeOffset.Now - WindowStart).TotalMinutes;
        Offset = new Vector(Math.Max(0, minutes * PixelsPerMinute - 120), _offset.Y);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        switch (e.Key)
        {
            case Key.Left:
                Offset = new Vector(_offset.X - ScrollSize.Width, _offset.Y);
                e.Handled = true;
                break;

            case Key.Right:
                Offset = new Vector(_offset.X + ScrollSize.Width, _offset.Y);
                e.Handled = true;
                break;

            case Key.Up:
                Offset = new Vector(_offset.X, _offset.Y - RowHeight);
                e.Handled = true;
                break;

            case Key.Down:
                Offset = new Vector(_offset.X, _offset.Y + RowHeight);
                e.Handled = true;
                break;

            case Key.PageUp:
                Offset = new Vector(_offset.X, _offset.Y - _viewport.Height);
                e.Handled = true;
                break;

            case Key.PageDown:
                Offset = new Vector(_offset.X, _offset.Y + _viewport.Height);
                e.Handled = true;
                break;

            case Key.Home:
                ScrollToNow();
                e.Handled = true;
                break;
        }
    }
}
