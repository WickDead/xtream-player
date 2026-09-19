using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XtreamPlayer.App.Behaviors;
using XtreamPlayer.Core.Abstractions;
using XtreamPlayer.Core.Library;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Core.Navigation;
using XtreamPlayer.Infrastructure.Xtream;

namespace XtreamPlayer.App.ViewModels;

/// <summary>
/// One row of poster tiles.
/// </summary>
/// <remarks>
/// <para>
/// Avalonia 11.3 ships no wrap-virtualizing panel: there is no
/// VirtualizingWrapPanel, and neither ItemsRepeater nor UniformGridLayout exists
/// anywhere in the framework assemblies. A plain WrapPanel as ItemsPanel does
/// not virtualize at all, and with 68,595 posters that alone makes the app
/// unusable.
/// </para>
/// <para>
/// So the flat item list is projected into rows of N and bound to an ordinary
/// ListBox over a VirtualizingStackPanel. Rows are exactly uniform in height,
/// virtualization is the battle-tested vertical path, and scroll restoration
/// becomes exact arithmetic: row = index / columns. The cost is re-chunking when
/// the window resizes, which is cheap because it only rebuilds the row wrappers
/// and not the items inside them.
/// </para>
/// </remarks>
public sealed class PosterRow<TItem>(int firstIndex, IReadOnlyList<TItem> items)
{
    public int FirstIndex { get; } = firstIndex;
    public IReadOnlyList<TItem> Items { get; } = items;
}

/// <summary>
/// A row in the category rail.
/// </summary>
public sealed partial class CategoryRowViewModel(CategoryRow row) : ObservableObject
{
    public CategorySelection Selection { get; } = row.Selection;
    public string Name { get; } = row.Name;
    public int Count { get; } = row.Count;
    public bool CanHide { get; } = row.CanHide;

    [ObservableProperty]
    private bool _isHidden = row.IsHidden;

    public string CountLabel => Count >= 1000 ? $"{Count / 1000.0:0.#}k" : Count.ToString();
}

/// <summary>
/// The browse behaviour shared by Live, VOD and Series.
/// </summary>
/// <remarks>
/// <para>
/// The previous app repeated all of this three times — in LivePage.tsx,
/// VodPage.tsx and SeriesPage.tsx — including the favorites lookup pair, the
/// hidden-category memo, the toggle handlers and the deep-link effect. Three
/// copies is three places to fix anything, and they had already drifted apart.
/// </para>
/// <para>
/// Filtering runs off the UI thread and writes results back in one batch. With
/// 68,595 items, doing it inline makes every keystroke in the search box drop
/// frames.
/// </para>
/// </remarks>
public abstract partial class BrowsePageViewModel<TItem> : PageViewModelBase, IDisposable
    where TItem : class
{
    private readonly IFavoritesStore _favorites;
    private readonly IHiddenCategoryRepository _hidden;
    private readonly ISettingsRepository _settings;
    private readonly ICatalogStore _catalogStore;

    private IReadOnlyList<TItem> _source = [];
    private IReadOnlyList<FilterableItem> _filterable = [];
    private IReadOnlyList<Category> _categories = [];
    private HashSet<string> _hiddenIds = new(StringComparer.Ordinal);

    private CancellationTokenSource? _filtering;
    private ProfileScope? _scope;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private CategoryRowViewModel? _selectedCategory;

    [ObservableProperty]
    private ItemSort _sort = ItemSort.Default;

    [ObservableProperty]
    private CategorySort _categorySort = CategorySort.ProviderOrder;

    [ObservableProperty]
    private bool _showHiddenCategories;

    [ObservableProperty]
    private TItem? _selectedItem;

    [ObservableProperty]
    private string _resultSummary = string.Empty;

    protected BrowsePageViewModel(
        IFavoritesStore favorites,
        IHiddenCategoryRepository hidden,
        ISettingsRepository settings,
        ICatalogStore catalogStore)
    {
        _favorites = favorites;
        _hidden = hidden;
        _settings = settings;
        _catalogStore = catalogStore;

        _favorites.Changed += OnFavoritesChanged;

        // Catalogs load in the background after the first page is already
        // on screen, and a page sitting on the navigation stack outlives
        // any number of syncs. Without this the page renders whatever was
        // loaded when it was first opened and never notices the real data
        // arriving.
        _catalogStore.Changed += OnCatalogChanged;
    }

    private void OnCatalogChanged(object? sender, CatalogState state) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => _ = ReloadFromCatalogAsync());

    /// <summary>Re-projects this page from the current catalog.</summary>
    protected abstract Task ReloadFromCatalogAsync();

    public ObservableCollection<CategoryRowViewModel> Categories { get; } = [];

    public ObservableCollection<TItem> Items { get; } = [];

    /// <summary>
    /// <see cref="Items"/> chunked into fixed-width rows, for grid pages. Empty
    /// for list pages, which bind to <see cref="Items"/> directly.
    /// </summary>
    public ObservableCollection<PosterRow<TItem>> Rows { get; } = [];

    /// <summary>
    /// Tiles per row. Grid views set this from their own width; 0 means this
    /// page is a list and does not use rows at all.
    /// </summary>
    public int ColumnCount
    {
        get => _columnCount;
        set
        {
            var clamped = Math.Max(0, value);

            if (_columnCount == clamped)
            {
                return;
            }

            _columnCount = clamped;
            OnPropertyChanged();
            RebuildRows();
        }
    }

    private int _columnCount;

    /// <summary>Re-chunks the current results into rows.</summary>
    private void RebuildRows()
    {
        if (ColumnCount <= 0)
        {
            if (Rows.Count > 0)
            {
                Rows.Clear();
            }

            return;
        }

        Rows.Clear();

        for (var start = 0; start < Items.Count; start += ColumnCount)
        {
            var take = Math.Min(ColumnCount, Items.Count - start);
            var chunk = new TItem[take];

            for (var i = 0; i < take; i++)
            {
                chunk[i] = Items[start + i];
            }

            Rows.Add(new PosterRow<TItem>(start, chunk));
        }
    }

    protected abstract ContentKind Kind { get; }

    protected abstract CategoryScope Scope { get; }

    /// <summary>Projects a catalog item down to what filtering needs.</summary>
    protected abstract FilterableItem Project(TItem item, int index);

    protected abstract string ItemIdOf(TItem item);

    /// <summary>Recent ids, newest first. Live overrides this; others have none.</summary>
    protected virtual IReadOnlyList<string> RecentIds => [];

    protected IFavoritesStore Favorites => _favorites;

    // ---- loading --------------------------------------------------------

    protected async Task SetSourceAsync(
        IReadOnlyList<TItem> items,
        IReadOnlyList<Category> categories,
        ProfileScope scope,
        CancellationToken ct = default)
    {
        _source = items;
        _categories = categories;
        _scope = scope;

        var projected = new FilterableItem[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            projected[i] = Project(items[i], i);
        }

        _filterable = projected;

        var hiddenRows = await _hidden.ListAsync(scope, Scope, ct).ConfigureAwait(false);
        var resolver = new HiddenCategoryResolver(hiddenRows);

        _hiddenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var category in categories)
        {
            if (resolver.IsHidden(category.Id, category.Name))
            {
                _hiddenIds.Add(category.Id);
            }
        }

        // Self-heal rows that matched by name because the provider renumbered.
        foreach (var repoint in resolver.Repoints)
        {
            await _hidden.RepointAsync(scope, Scope, repoint.OldCategoryId, repoint.NewCategoryId, ct)
                .ConfigureAwait(false);
        }

        RebuildRail();
        await RestoreSelectionAsync(ct).ConfigureAwait(false);
        ApplyFilter();
    }

    private void RebuildRail()
    {
        var previous = SelectedCategory?.Selection;

        var rows = CatalogFilter.BuildRail(
            _categories,
            _hiddenIds,
            CategorySort,
            ShowHiddenCategories,
            totalCount: _source.Count,
            favoriteCount: _favorites.IdsFor(Kind).Count,
            recentCount: RecentIds.Count);

        Categories.Clear();
        foreach (var row in rows)
        {
            Categories.Add(new CategoryRowViewModel(row));
        }

        // Keep the user where they were across a rail rebuild.
        SelectedCategory = Categories.FirstOrDefault(c => c.Selection == previous)
                           ?? Categories.FirstOrDefault();
    }

    private async Task RestoreSelectionAsync(CancellationToken ct)
    {
        var key = await _settings.GetAsync(SelectionSettingKey, ct).ConfigureAwait(false);

        if (key is null)
        {
            return;
        }

        var match = Categories.FirstOrDefault(c => c.Selection.PersistKey == key);
        if (match is not null)
        {
            SelectedCategory = match;
        }
    }

    private string SelectionSettingKey => $"browse.{Scope.ToStorageString()}.category";

    // ---- filtering ------------------------------------------------------

    /// <summary>
    /// Re-runs the filter off the UI thread.
    /// </summary>
    /// <remarks>
    /// Cancelling the previous run matters while typing: without it, each
    /// keystroke queues a full pass over the catalog and they pile up.
    /// </remarks>
    protected void ApplyFilter()
    {
        _filtering?.Cancel();
        _filtering?.Dispose();

        var cts = new CancellationTokenSource();
        _filtering = cts;

        var request = new FilterRequest
        {
            Selection = SelectedCategory?.Selection ?? CategorySelection.All.Instance,
            SearchText = SearchText,
            Sort = Sort,
            FavoriteIds = _favorites.IdsFor(Kind),
            HiddenCategoryIds = _hiddenIds,
            RecentIds = RecentIds,
        };

        var snapshot = _filterable;
        var source = _source;

        _ = Task.Run(() =>
        {
            var indexes = CatalogFilter.Apply(snapshot, request);

            if (cts.IsCancellationRequested)
            {
                return;
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                Items.Clear();
                foreach (var index in indexes)
                {
                    Items.Add(source[index]);
                }

                RebuildRows();

                ResultSummary = indexes.Count == source.Count
                    ? $"{indexes.Count:N0} items"
                    : $"{indexes.Count:N0} of {source.Count:N0}";

                // A changed result set invalidates any saved scroll offset.
                ScrollPositions.Remove(ListScrollKey);
            });
        }, cts.Token);
    }

    protected virtual string ListScrollKey => "items";

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSortChanged(ItemSort value) => ApplyFilter();

    partial void OnSelectedCategoryChanged(CategoryRowViewModel? value)
    {
        ApplyFilter();

        if (value is not null)
        {
            // Remembered so returning to this catalog lands where it was left.
            _ = _settings.SetAsync(SelectionSettingKey, value.Selection.PersistKey);
        }
    }

    partial void OnCategorySortChanged(CategorySort value) => RebuildRail();

    /// <summary>
    /// Derived pages react to selection here.
    /// </summary>
    /// <remarks>
    /// A virtual hook rather than the generated partial, because SelectedItem is
    /// declared on this base class and its generated OnSelectedItemChanged
    /// therefore belongs to this type, not to the derived one.
    /// </remarks>
    protected virtual void OnItemSelected(TItem? item)
    {
    }

    partial void OnSelectedItemChanged(TItem? value) => OnItemSelected(value);

    partial void OnShowHiddenCategoriesChanged(bool value) => RebuildRail();

    // ---- commands -------------------------------------------------------

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private async Task ToggleFavoriteAsync(TItem? item)
    {
        if (item is null)
        {
            return;
        }

        await _favorites.ToggleAsync(Kind, ItemIdOf(item), TitleOf(item)).ConfigureAwait(false);
    }

    protected abstract string TitleOf(TItem item);

    [RelayCommand]
    private async Task ToggleCategoryHiddenAsync(CategoryRowViewModel? row)
    {
        if (row is null || _scope is not { } scope || row.Selection is not CategorySelection.Real real)
        {
            return;
        }

        if (row.IsHidden)
        {
            await _hidden.UnhideAsync(scope, Scope, real.CategoryId).ConfigureAwait(false);
            _hiddenIds.Remove(real.CategoryId);
            row.IsHidden = false;
        }
        else
        {
            await _hidden.HideAsync(scope, Scope, real.CategoryId, real.Name).ConfigureAwait(false);
            _hiddenIds.Add(real.CategoryId);
            row.IsHidden = true;
        }

        RebuildRail();
        ApplyFilter();
    }

    [RelayCommand]
    private void ToggleShowHidden() => ShowHiddenCategories = !ShowHiddenCategories;

    public bool IsFavorite(string contentId) => _favorites.IsFavorite(Kind, contentId);

    private void OnFavoritesChanged(object? sender, FavoritesChangedEventArgs e)
    {
        if (e.Kind != Kind)
        {
            return;
        }

        // The rail shows a favorites count, and the Favorites view itself must
        // drop an item the moment it is unstarred.
        RebuildRail();

        if (SelectedCategory?.Selection is CategorySelection.Favorites)
        {
            ApplyFilter();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _favorites.Changed -= OnFavoritesChanged;
            _catalogStore.Changed -= OnCatalogChanged;
            _filtering?.Cancel();
            _filtering?.Dispose();
        }

        base.Dispose(disposing);
    }
}
