using Xunit;
using XtreamPlayer.Core.Library;

namespace XtreamPlayer.Tests;

/// <summary>
/// The precomputed-order fast path must be indistinguishable from sorting.
/// </summary>
/// <remarks>
/// This exists because the fast path is an optimisation, and an optimisation
/// that quietly reorders or drops results is worse than the latency it removed.
/// Every case here runs both paths and demands the same answer.
/// </remarks>
public sealed class CatalogOrderingTests
{
    private static List<FilterableItem> Catalog(int count = 500)
    {
        // Deliberately awkward: repeated names, numbers that expose a plain
        // alphabetical sort, missing dates, and mixed case for the search path.
        var names = new[]
        {
            "The Matrix", "the matrix", "TSN 1", "TSN 2", "TSN 10", "Alien",
            "ALIEN 2", "Zulu", "apple", "Apple Pie", "Ödipus", "007 Bond",
        };

        var items = new List<FilterableItem>(count);

        for (var i = 0; i < count; i++)
        {
            items.Add(new FilterableItem(
                Id: $"id-{i}",
                Name: $"{names[i % names.Length]} {i % 7}",
                CategoryId: $"cat-{i % 5}",
                CategoryOrder: i % 5,
                Added: i % 3 == 0 ? null : new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(i % 90),
                SourceIndex: i));
        }

        return items;
    }

    public static TheoryData<ItemSort, string?, string?> Cases()
    {
        var data = new TheoryData<ItemSort, string?, string?>();

        foreach (var sort in new[] { ItemSort.Default, ItemSort.Name, ItemSort.Newest })
        {
            foreach (var search in new string?[] { null, "", "a", "the", "TSN", "ödipus", "zzzz" })
            {
                foreach (var category in new string?[] { null, "cat-2" })
                {
                    data.Add(sort, search, category);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void FastPathMatchesSortingExactly(ItemSort sort, string? search, string? categoryId)
    {
        var items = Catalog();
        var ordering = new CatalogOrdering(items);

        var request = new FilterRequest
        {
            Selection = categoryId is null
                ? CategorySelection.All.Instance
                : new CategorySelection.Real(categoryId, "Category", 2),
            SearchText = search,
            Sort = sort,
        };

        var sorted = CatalogFilter.Apply(items, request);
        var precomputed = CatalogFilter.Apply(items, request, ordering);

        Assert.Equal(sorted, precomputed);
    }

    [Fact]
    public void FastPathMatchesForFavorites()
    {
        var items = Catalog();
        var ordering = new CatalogOrdering(items);

        var request = new FilterRequest
        {
            Selection = CategorySelection.Favorites.Instance,
            Sort = ItemSort.Name,
            FavoriteIds = new HashSet<string>(
                Enumerable.Range(0, 40).Select(i => $"id-{i * 3}"), StringComparer.Ordinal),
        };

        Assert.Equal(
            CatalogFilter.Apply(items, request),
            CatalogFilter.Apply(items, request, ordering));
    }

    [Fact]
    public void RecentStillSortsByTheCallersOrderRatherThanTheCatalogs()
    {
        // Recency comes from the caller, so it cannot be precomputed. Passing an
        // ordering must not silently switch it to catalog order.
        var items = Catalog(60);
        var ordering = new CatalogOrdering(items);

        var recent = new List<string> { "id-40", "id-3", "id-27" };

        var request = new FilterRequest
        {
            Selection = CategorySelection.Recent.Instance,
            Sort = ItemSort.Default,
            RecentIds = recent,
        };

        var result = CatalogFilter.Apply(items, request, ordering);

        Assert.Equal([40, 3, 27], result);
    }

    [Fact]
    public void IgnoresAnOrderingBuiltFromADifferentList()
    {
        // A stale ordering would otherwise index into the wrong catalog and
        // return results for items the caller never passed.
        var items = Catalog(50);
        var other = Catalog(90);

        var request = new FilterRequest
        {
            Selection = CategorySelection.All.Instance,
            Sort = ItemSort.Name,
        };

        var result = CatalogFilter.Apply(items, request, new CatalogOrdering(other));

        Assert.Equal(CatalogFilter.Apply(items, request), result);
        Assert.All(result, index => Assert.InRange(index, 0, items.Count - 1));
    }

    [Fact]
    public void BuildsEachSortOrderOnlyOnce()
    {
        var items = Catalog(200);
        var ordering = new CatalogOrdering(items);

        Assert.Same(ordering.For(ItemSort.Name), ordering.For(ItemSort.Name));
        Assert.Same(ordering.SearchNames, ordering.SearchNames);
        Assert.NotSame(ordering.For(ItemSort.Name), ordering.For(ItemSort.Newest));
    }

    [Fact]
    public void SearchIsStillCaseInsensitive()
    {
        var items = Catalog(120);
        var ordering = new CatalogOrdering(items);

        FilterRequest Request(string text) => new()
        {
            Selection = CategorySelection.All.Instance,
            SearchText = text,
            Sort = ItemSort.Name,
        };

        var lower = CatalogFilter.Apply(items, Request("matrix"), ordering);
        var upper = CatalogFilter.Apply(items, Request("MATRIX"), ordering);
        var mixed = CatalogFilter.Apply(items, Request("MaTrIx"), ordering);

        Assert.NotEmpty(lower);
        Assert.Equal(lower, upper);
        Assert.Equal(lower, mixed);
    }
}
