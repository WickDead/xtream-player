using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using XtreamPlayer.Core.Navigation;

namespace XtreamPlayer.App.Behaviors;

/// <summary>
/// Something a list can identify its items by, so a saved position survives the
/// collection changing underneath it.
/// </summary>
public interface IScrollKeyed
{
    string ScrollKey { get; }
}

/// <summary>
/// Saves and restores a list's scroll position across navigation.
/// </summary>
/// <remarks>
/// <para>
/// The problem this exists to solve is not obvious. <c>ScrollViewer.Offset</c>
/// clamps against <c>Extent</c>, and when a cached view is re-attached the
/// virtualizing panel has unrealized its containers and reports an extent barely
/// larger than the viewport until it measures. Setting <c>Offset = (0, 8400)</c>
/// at that moment silently becomes <c>(0, 0)</c>. Caching the view instance does
/// not help — Avalonia 11 unrealizes on detach.
/// </para>
/// <para>
/// So restoration proceeds in tiers, cheapest and most robust first, and the
/// pixel step is gated on the extent actually being large enough rather than on
/// a timer. A <c>Task.Delay</c> guess works on the machine it was written on and
/// fails on a slower one.
/// </para>
/// </remarks>
public static class ScrollAnchor
{
    private const int MaxRefinementPasses = 10;

    /// <summary>Identifies this list's saved position within its page.</summary>
    public static readonly AttachedProperty<string?> KeyProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Key", typeof(ScrollAnchor));

    /// <summary>The page whose ScrollPositions dictionary holds the state.</summary>
    public static readonly AttachedProperty<PageViewModelBase?> PageProperty =
        AvaloniaProperty.RegisterAttached<Control, PageViewModelBase?>("Page", typeof(ScrollAnchor));

    /// <summary>
    /// Columns in the layout, for grids. Restoring pixels across a resize is
    /// wrong; the index has to be re-projected through the new column count.
    /// </summary>
    public static readonly AttachedProperty<int> ColumnsProperty =
        AvaloniaProperty.RegisterAttached<Control, int>("Columns", typeof(ScrollAnchor), 1);

    public static string? GetKey(Control control) => control.GetValue(KeyProperty);
    public static void SetKey(Control control, string? value) => control.SetValue(KeyProperty, value);

    public static PageViewModelBase? GetPage(Control control) => control.GetValue(PageProperty);
    public static void SetPage(Control control, PageViewModelBase? value) =>
        control.SetValue(PageProperty, value);

    public static int GetColumns(Control control) => control.GetValue(ColumnsProperty);
    public static void SetColumns(Control control, int value) => control.SetValue(ColumnsProperty, value);

    static ScrollAnchor()
    {
        PageProperty.Changed.AddClassHandler<Control>((control, args) =>
        {
            if (args.OldValue is PageViewModelBase previous)
            {
                Detach(control, previous);
            }

            if (args.NewValue is PageViewModelBase next)
            {
                Attach(control, next);
            }
        });
    }

    private static readonly Dictionary<Control, EventHandler> Handlers = [];

    private static void Attach(Control control, PageViewModelBase page)
    {
        void OnRestoreRequested(object? sender, EventArgs e) =>
            Dispatcher.UIThread.Post(() => Restore(control, page), DispatcherPriority.Loaded);

        Handlers[control] = OnRestoreRequested;
        page.ScrollRestoreRequested += OnRestoreRequested;

        // Capture happens on detach, which covers both navigating away and the
        // window closing. Doing it later is too late: the offsets are gone.
        control.DetachedFromVisualTree += (_, _) => Capture(control, page);

        control.AttachedToVisualTree += (_, _) =>
            Dispatcher.UIThread.Post(() => Restore(control, page), DispatcherPriority.Loaded);
    }

    private static void Detach(Control control, PageViewModelBase page)
    {
        if (Handlers.Remove(control, out var handler))
        {
            page.ScrollRestoreRequested -= handler;
        }
    }

    // ---- capture --------------------------------------------------------

    /// <summary>
    /// Records four things, not one: pixels alone are meaningless if the
    /// collection or the layout changed while the page was away.
    /// </summary>
    public static void Capture(Control control, PageViewModelBase page)
    {
        var key = GetKey(control);
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        var scroller = FindScrollViewer(control);
        if (scroller is null)
        {
            return;
        }

        var (index, itemKey) = FirstVisibleItem(control, scroller);

        page.ScrollPositions[key] = new ScrollState(
            scroller.Offset.Y,
            index,
            itemKey,
            Math.Max(1, GetColumns(control)));
    }

    // ---- restore --------------------------------------------------------

    public static void Restore(Control control, PageViewModelBase page)
    {
        var key = GetKey(control);
        if (string.IsNullOrEmpty(key)
            || !page.ScrollPositions.TryGetValue(key, out var saved)
            || !saved.IsMeaningful)
        {
            return;
        }

        var scroller = FindScrollViewer(control);
        if (scroller is null)
        {
            return;
        }

        var items = ItemsOf(control);
        var targetIndex = saved.FirstIndex;
        var usePixels = true;

        // Tier 1: reconcile by key. If the collection changed while we were away
        // — a favorite removed, a sync landed — the saved index points at the
        // wrong item and the saved pixels are meaningless.
        if (saved.FirstItemKey is { } savedKey && items is not null)
        {
            var found = IndexOfKey(items, savedKey);

            if (found >= 0 && found != saved.FirstIndex)
            {
                targetIndex = found;
                usePixels = false;
            }
            else if (found < 0)
            {
                // The anchor item is gone entirely; the index is the best guess.
                usePixels = false;
            }
        }

        // Tier 2: reconcile by column count. The window can be resized while a
        // detail page is open, and reusing pixels across that is simply wrong.
        var columns = Math.Max(1, GetColumns(control));
        if (columns != saved.ColumnCount)
        {
            usePixels = false;
        }

        // Tier 3: coarse scroll by index. Forces the virtualizing panel to
        // realize toward the target, and works with non-uniform item heights.
        ScrollToIndex(control, targetIndex);

        if (!usePixels)
        {
            return;
        }

        // Tier 4: pixel refinement, gated on the extent being large enough for
        // the offset to survive clamping. This gate is the whole trick.
        RefineWhenLaidOut(scroller, saved.OffsetY);
    }

    private static void RefineWhenLaidOut(ScrollViewer scroller, double targetOffset)
    {
        var passes = 0;

        void OnLayoutUpdated(object? sender, EventArgs e)
        {
            passes++;

            if (scroller.Extent.Height >= targetOffset + scroller.Viewport.Height)
            {
                scroller.LayoutUpdated -= OnLayoutUpdated;
                scroller.Offset = scroller.Offset.WithY(targetOffset);
                return;
            }

            if (passes >= MaxRefinementPasses)
            {
                // Leave it at the coarse position rather than fight layout.
                scroller.LayoutUpdated -= OnLayoutUpdated;
            }
        }

        scroller.LayoutUpdated += OnLayoutUpdated;

        // Nudge layout so the handler runs even if nothing else changes.
        scroller.InvalidateMeasure();
    }

    // ---- control plumbing -----------------------------------------------

    private static ScrollViewer? FindScrollViewer(Control control) => control switch
    {
        ScrollViewer scroller => scroller,
        ListBox listBox => listBox.Scroll as ScrollViewer,
        _ => control.FindDescendantOfType<ScrollViewer>(),
    };

    private static System.Collections.IList? ItemsOf(Control control) => control switch
    {
        ItemsControl items => items.ItemsSource as System.Collections.IList
                              ?? (System.Collections.IList?)items.Items,
        ScrollViewer { Content: Control inner } => ItemsOf(inner),
        _ => null,
    };

    private static int IndexOfKey(System.Collections.IList items, string key)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is IScrollKeyed keyed
                && string.Equals(keyed.ScrollKey, key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static (int Index, string? Key) FirstVisibleItem(Control control, ScrollViewer scroller)
    {
        var items = ItemsOf(control);
        if (items is null || items.Count == 0)
        {
            return (0, null);
        }

        if (control is ItemsControl itemsControl)
        {
            // Walk realized containers and take the first one still inside the
            // viewport. Cheaper and more accurate than estimating from pixels.
            for (var i = 0; i < items.Count; i++)
            {
                if (itemsControl.ContainerFromIndex(i) is not Control container)
                {
                    continue;
                }

                var bounds = container.Bounds;
                if (bounds.Bottom > scroller.Offset.Y)
                {
                    return (i, (items[i] as IScrollKeyed)?.ScrollKey);
                }
            }
        }

        return (0, (items[0] as IScrollKeyed)?.ScrollKey);
    }

    private static void ScrollToIndex(Control control, int index)
    {
        switch (control)
        {
            case ListBox listBox when index >= 0 && index < listBox.ItemCount:
                listBox.ScrollIntoView(index);
                break;

            case ItemsControl items when index >= 0 && index < items.ItemCount:
                items.ScrollIntoView(index);
                break;
        }
    }
}
