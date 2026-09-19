using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace XtreamPlayer.App.Controls;

/// <summary>
/// The time ruler above the guide.
/// </summary>
/// <remarks>
/// Drawn rather than templated, for the same reason as the grid itself: a
/// thirty-hour window at half-hour resolution is sixty labels, redrawn on every
/// horizontal scroll. It shares the canvas's offset so the two cannot drift.
/// </remarks>
public sealed class EpgTimeRuler : Control
{
    private EpgTimelineCanvas? _source;

    public EpgTimelineCanvas? Source
    {
        get => _source;
        set
        {
            if (_source is not null)
            {
                _source.OffsetChanged -= OnSourceChanged;
                _source.RowsChanged -= OnSourceChanged;
            }

            _source = value;

            if (_source is not null)
            {
                _source.OffsetChanged += OnSourceChanged;

                // Scrolling is not the only thing that changes what belongs in
                // this column: filtering the guide by category replaces the rows
                // outright while the offset stays put.
                _source.RowsChanged += OnSourceChanged;
            }

            InvalidateVisual();
        }
    }

    private void OnSourceChanged(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        if (_source is null || Bounds.Width <= 0)
        {
            return;
        }

        var text = this.TryFindResource("TextMuted", out var muted) && muted is IBrush mutedBrush
            ? mutedBrush
            : Brushes.Gray;

        var live = this.TryFindResource("LiveDot", out var dot) && dot is IBrush liveBrush
            ? liveBrush
            : Brushes.Red;

        var pixelsPerMinute = _source.PixelsPerMinute;
        var offset = _source.Offset.X;
        var windowStart = _source.WindowStart;

        // Label every half hour, or every two hours when zoomed far out, so the
        // ruler never becomes an unreadable smear.
        var stepMinutes = pixelsPerMinute >= 6 ? 30 : pixelsPerMinute >= 3 ? 60 : 120;

        var firstMinute = Math.Floor(offset / pixelsPerMinute / stepMinutes) * stepMinutes;
        var lastMinute = (offset + Bounds.Width) / pixelsPerMinute;

        for (var minute = firstMinute; minute <= lastMinute; minute += stepMinutes)
        {
            var x = minute * pixelsPerMinute - offset;

            if (x < -60 || x > Bounds.Width)
            {
                continue;
            }

            var at = windowStart.AddMinutes(minute).ToLocalTime();

            context.FillRectangle(text, new Rect(x, Bounds.Height - 6, 1, 6));

            var label = new FormattedText(
                at.Minute == 0 ? at.ToString("HH:mm") : at.ToString("HH:mm"),
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                Typeface.Default,
                11,
                text);

            context.DrawText(label, new Point(x + 4, 6));
        }

        var nowX = (DateTimeOffset.Now - windowStart).TotalMinutes * pixelsPerMinute - offset;

        if (nowX >= 0 && nowX <= Bounds.Width)
        {
            context.FillRectangle(live, new Rect(nowX - 1, 0, 2, Bounds.Height));
        }
    }
}

/// <summary>
/// The pinned channel column beside the guide.
/// </summary>
public sealed class EpgChannelColumn : Control
{
    private const double RowHeight = 56;

    private EpgTimelineCanvas? _source;

    public EpgTimelineCanvas? Source
    {
        get => _source;
        set
        {
            if (_source is not null)
            {
                _source.OffsetChanged -= OnSourceChanged;
            }

            _source = value;

            if (_source is not null)
            {
                _source.OffsetChanged += OnSourceChanged;
            }

            InvalidateVisual();
        }
    }

    private void OnSourceChanged(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        if (_source is null || _source.Rows.Count == 0 || Bounds.Height <= 0)
        {
            return;
        }

        var text = this.TryFindResource("TextPrimary", out var primary) && primary is IBrush primaryBrush
            ? primaryBrush
            : Brushes.White;

        var divider = this.TryFindResource("Divider", out var line) && line is IBrush lineBrush
            ? lineBrush
            : Brushes.DimGray;

        var offset = _source.Offset.Y;
        var rows = _source.Rows;

        var first = Math.Max(0, (int)(offset / RowHeight));
        var last = Math.Min(rows.Count - 1, (int)((offset + Bounds.Height) / RowHeight));

        for (var i = first; i <= last; i++)
        {
            var y = i * RowHeight - offset;

            var label = new FormattedText(
                rows[i].ChannelName,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                Typeface.Default,
                12.5,
                text)
            {
                MaxTextWidth = Math.Max(10, Bounds.Width - 20),
                MaxLineCount = 2,
                Trimming = TextTrimming.CharacterEllipsis,
            };

            context.DrawText(label, new Point(12, y + 10));

            context.FillRectangle(divider, new Rect(0, y + RowHeight - 1, Bounds.Width, 0.5));
        }
    }
}
