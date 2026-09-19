using XtreamPlayer.Core.Models;

namespace XtreamPlayer.Core.Library;

/// <summary>
/// One catalog item, reduced to what filtering and sorting need.
/// </summary>
/// <remarks>
/// A projection rather than a generic constraint on the three catalog records,
/// so the filter pipeline is written and tested once instead of three times.
/// </remarks>
public readonly record struct FilterableItem(
    string Id,
    string Name,
    string CategoryId,
    int CategoryOrder,
    DateTimeOffset? Added,
    int SourceIndex);

public sealed record FilterRequest
{
    public required CategorySelection Selection { get; init; }
    public string? SearchText { get; init; }
    public ItemSort Sort { get; init; } = ItemSort.Default;

    /// <summary>Content ids favorited in this catalog, for the Favorites view.</summary>
    public IReadOnlySet<string> FavoriteIds { get; init; } = new HashSet<string>();

    /// <summary>Hidden category ids, excluded from All.</summary>
    public IReadOnlySet<string> HiddenCategoryIds { get; init; } = new HashSet<string>();

    /// <summary>Recently watched ids, newest first. Live only.</summary>
    public IReadOnlyList<string> RecentIds { get; init; } = [];
}

/// <summary>
/// The filter and sort pipeline shared by Live, VOD and Series.
/// </summary>
/// <remarks>
/// The previous app repeated this logic — the favorites memo pair, the
/// hidden-category memo, the category projection and the sort — in
/// LivePage.tsx, VodPage.tsx and SeriesPage.tsx. Three copies meant three places
/// to fix anything, and they had already drifted.
/// </remarks>
public static class CatalogFilter
{
    /// <summary>
    /// Applies selection, search and sort in one pass.
    /// </summary>
    public static List<int> Apply(IReadOnlyList<FilterableItem> items, FilterRequest request)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(request);

        var matches = new List<FilterableItem>(Math.Min(items.Count, 4096));

        var search = request.SearchText?.Trim();
        var hasSearch = !string.IsNullOrEmpty(search) && search.Length >= 2;

        foreach (var item in items)
        {
            if (!MatchesSelection(item, request))
            {
                continue;
            }

            if (hasSearch && !item.Name.Contains(search!, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matches.Add(item);
        }

        Sort(matches, request);

        var indexes = new List<int>(matches.Count);
        foreach (var match in matches)
        {
            indexes.Add(match.SourceIndex);
        }

        return indexes;
    }

    private static bool MatchesSelection(FilterableItem item, FilterRequest request) =>
        request.Selection switch
        {
            CategorySelection.Favorites => request.FavoriteIds.Contains(item.Id),
            CategorySelection.Recent => request.RecentIds.Contains(item.Id),
            CategorySelection.Real real => item.CategoryId == real.CategoryId,

            // Hidden categories are excluded from All but remain reachable by
            // selecting them directly, which is what "hidden" should mean.
            CategorySelection.All => !request.HiddenCategoryIds.Contains(item.CategoryId),

            _ => true,
        };

    private static void Sort(List<FilterableItem> items, FilterRequest request)
    {
        switch (request.Sort)
        {
            case ItemSort.Name:
                // Natural, not plain alphabetical: catalogs are full of numbered
                // names and "TSN 10" must not land between "TSN 1" and "TSN 2".
                items.Sort(static (a, b) => NaturalComparer.Instance.Compare(a.Name, b.Name));
                break;

            case ItemSort.Newest:
                items.Sort(static (a, b) =>
                {
                    // Items with no date sort last rather than jumbling with 1970.
                    var byDate = (b.Added ?? DateTimeOffset.MinValue)
                        .CompareTo(a.Added ?? DateTimeOffset.MinValue);

                    return byDate != 0
                        ? byDate
                        : NaturalComparer.Instance.Compare(a.Name, b.Name);
                });
                break;

            case ItemSort.Default:
            default:
                if (request.Selection is CategorySelection.Recent)
                {
                    // Recency order comes from the caller's list, not the catalog.
                    var order = request.RecentIds
                        .Select((id, index) => (id, index))
                        .ToDictionary(x => x.id, x => x.index, StringComparer.Ordinal);

                    items.Sort((a, b) =>
                        order.GetValueOrDefault(a.Id, int.MaxValue)
                            .CompareTo(order.GetValueOrDefault(b.Id, int.MaxValue)));
                }
                else
                {
                    // Provider order: category first, then the provider's own
                    // item ordering within it.
                    items.Sort(static (a, b) =>
                    {
                        var byCategory = a.CategoryOrder.CompareTo(b.CategoryOrder);
                        return byCategory != 0 ? byCategory : a.SourceIndex.CompareTo(b.SourceIndex);
                    });
                }

                break;
        }
    }

    /// <summary>
    /// Builds the category rail.
    /// </summary>
    /// <remarks>
    /// Hidden categories are still returned, flagged, so the rail can reveal
    /// them on demand rather than making the setting feel like deletion.
    /// </remarks>
    public static List<CategoryRow> BuildRail(
        IReadOnlyList<Category> categories,
        IReadOnlySet<string> hiddenCategoryIds,
        CategorySort sort,
        bool showHidden,
        int totalCount,
        int favoriteCount,
        int recentCount = -1)
    {
        var rows = new List<CategoryRow>(categories.Count + 3)
        {
            new()
            {
                Selection = CategorySelection.All.Instance,
                Name = "All",
                Count = totalCount,
            },
        };

        if (favoriteCount > 0)
        {
            rows.Add(new CategoryRow
            {
                Selection = CategorySelection.Favorites.Instance,
                Name = "Favorites",
                Count = favoriteCount,
            });
        }

        if (recentCount > 0)
        {
            rows.Add(new CategoryRow
            {
                Selection = CategorySelection.Recent.Instance,
                Name = "Recent",
                Count = recentCount,
            });
        }

        var real = new List<CategoryRow>(categories.Count);

        foreach (var category in categories)
        {
            var hidden = hiddenCategoryIds.Contains(category.Id);

            if (hidden && !showHidden)
            {
                continue;
            }

            real.Add(new CategoryRow
            {
                Selection = new CategorySelection.Real(category.Id, category.Name, category.Order),
                Name = category.Name,
                Count = category.ItemCount,
                IsHidden = hidden,
            });
        }

        switch (sort)
        {
            case CategorySort.Name:
                real.Sort(static (a, b) => NaturalComparer.Instance.Compare(a.Name, b.Name));
                break;

            case CategorySort.Size:
                real.Sort(static (a, b) => b.Count.CompareTo(a.Count));
                break;

            case CategorySort.ProviderOrder:
            default:
                real.Sort(static (a, b) =>
                    ((CategorySelection.Real)a.Selection).Order
                    .CompareTo(((CategorySelection.Real)b.Selection).Order));
                break;
        }

        rows.AddRange(real);
        return rows;
    }
}
