using XtreamPlayer.Core.Library;
using XtreamPlayer.Core.Models;
using Xunit;

namespace XtreamPlayer.Tests;

public sealed class CatalogFilterTests
{
    private static readonly FilterableItem[] Items =
    [
        new("1", "Sportsnet 360", "sports", 0, Date(2026, 1, 1), 0),
        new("2", "TSN 1", "sports", 0, Date(2026, 3, 1), 1),
        new("3", "BBC One", "uk", 1, Date(2025, 6, 1), 2),
        new("4", "Cartoon Network", "kids", 2, null, 3),
        new("5", "sports documentary", "docs", 3, Date(2026, 2, 1), 4),
    ];

    private static DateTimeOffset Date(int y, int m, int d) => new(y, m, d, 0, 0, 0, TimeSpan.Zero);

    private static FilterRequest Request(
        CategorySelection? selection = null,
        string? search = null,
        ItemSort sort = ItemSort.Default,
        string[]? favorites = null,
        string[]? hidden = null,
        string[]? recent = null) => new()
    {
        Selection = selection ?? CategorySelection.All.Instance,
        SearchText = search,
        Sort = sort,
        FavoriteIds = new HashSet<string>(favorites ?? [], StringComparer.Ordinal),
        HiddenCategoryIds = new HashSet<string>(hidden ?? [], StringComparer.Ordinal),
        RecentIds = recent ?? [],
    };

    private static string[] Names(IEnumerable<int> indexes) =>
        [.. indexes.Select(i => Items[i].Name)];

    [Fact]
    public void AllReturnsEverything() =>
        Assert.Equal(5, CatalogFilter.Apply(Items, Request()).Count);

    [Fact]
    public void ARealCategoryFiltersToItsItems() =>
        Assert.Equal(
            ["Sportsnet 360", "TSN 1"],
            Names(CatalogFilter.Apply(Items, Request(new CategorySelection.Real("sports", "Sports", 0)))));

    [Fact]
    public void FavoritesFilterByContentId() =>
        Assert.Equal(
            ["TSN 1", "Cartoon Network"],
            Names(CatalogFilter.Apply(Items, Request(
                CategorySelection.Favorites.Instance, favorites: ["2", "4"]))));

    /// <summary>
    /// Hiding removes a category from All but must not make it unreachable —
    /// otherwise "hide" behaves like "delete".
    /// </summary>
    [Fact]
    public void HiddenCategoriesAreExcludedFromAllButStillSelectable()
    {
        var withoutKids = CatalogFilter.Apply(Items, Request(hidden: ["kids"]));
        Assert.DoesNotContain("Cartoon Network", Names(withoutKids));

        var selectedDirectly = CatalogFilter.Apply(Items, Request(
            new CategorySelection.Real("kids", "Kids", 2), hidden: ["kids"]));

        Assert.Equal(["Cartoon Network"], Names(selectedDirectly));
    }

    [Fact]
    public void SearchIsCaseInsensitiveAndMatchesAnywhere() =>
        Assert.Equal(
            // "sports " sorts before "sportsn" because a space precedes a letter.
            ["sports documentary", "Sportsnet 360"],
            Names(CatalogFilter.Apply(Items, Request(search: "SPORTS", sort: ItemSort.Name))));

    [Theory]
    // A single character matches far too much to be useful while typing.
    [InlineData("s")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ShortSearchTermsAreIgnored(string? search) =>
        Assert.Equal(5, CatalogFilter.Apply(Items, Request(search: search)).Count);

    [Fact]
    public void SearchCombinesWithTheSelectedCategory() =>
        Assert.Equal(
            ["Sportsnet 360"],
            Names(CatalogFilter.Apply(Items, Request(
                new CategorySelection.Real("sports", "Sports", 0), search: "360"))));

    [Fact]
    public void NameSortIsNaturalAndCaseInsensitive() =>
        Assert.Equal(
            ["BBC One", "Cartoon Network", "sports documentary", "Sportsnet 360", "TSN 1"],
            Names(CatalogFilter.Apply(Items, Request(sort: ItemSort.Name))));

    /// <summary>
    /// Undated items sort last rather than mixing in as 1970, which is what a
    /// naive comparison does.
    /// </summary>
    [Fact]
    public void NewestSortPutsUndatedItemsLast()
    {
        var sorted = Names(CatalogFilter.Apply(Items, Request(sort: ItemSort.Newest)));

        Assert.Equal("TSN 1", sorted[0]);
        Assert.Equal("Cartoon Network", sorted[^1]);
    }

    [Fact]
    public void DefaultSortFollowsProviderOrder() =>
        Assert.Equal(
            ["Sportsnet 360", "TSN 1", "BBC One", "Cartoon Network", "sports documentary"],
            Names(CatalogFilter.Apply(Items, Request())));

    [Fact]
    public void RecentUsesTheCallersOrderNotTheCatalogs() =>
        Assert.Equal(
            ["Cartoon Network", "Sportsnet 360"],
            Names(CatalogFilter.Apply(Items, Request(
                CategorySelection.Recent.Instance, recent: ["4", "1"]))));

    [Fact]
    public void AnEmptyCatalogIsHandled() =>
        Assert.Empty(CatalogFilter.Apply([], Request()));
}

public sealed class CategoryRailTests
{
    private static readonly Category[] Categories =
    [
        new() { Id = "sports", Name = "Sports", Order = 0, ItemCount = 40 },
        new() { Id = "uk", Name = "UK", Order = 1, ItemCount = 120 },
        new() { Id = "kids", Name = "Kids", Order = 2, ItemCount = 10 },
    ];

    private static List<CategoryRow> Build(
        CategorySort sort = CategorySort.ProviderOrder,
        bool showHidden = false,
        string[]? hidden = null,
        int favorites = 0,
        int recent = -1) =>
        CatalogFilter.BuildRail(
            Categories,
            new HashSet<string>(hidden ?? [], StringComparer.Ordinal),
            sort, showHidden, totalCount: 170, favoriteCount: favorites, recentCount: recent);

    [Fact]
    public void AllIsAlwaysFirst()
    {
        var rail = Build();

        Assert.Equal("All", rail[0].Name);
        Assert.Equal(170, rail[0].Count);
    }

    [Fact]
    public void FavoritesAndRecentOnlyAppearWhenTheyHaveContent()
    {
        Assert.DoesNotContain(Build(), r => r.Name == "Favorites");
        Assert.Contains(Build(favorites: 21), r => r.Name == "Favorites");

        Assert.DoesNotContain(Build(), r => r.Name == "Recent");
        Assert.Contains(Build(recent: 5), r => r.Name == "Recent");
    }

    [Fact]
    public void HiddenCategoriesAreOmittedUntilRevealed()
    {
        Assert.DoesNotContain(Build(hidden: ["kids"]), r => r.Name == "Kids");

        var revealed = Build(showHidden: true, hidden: ["kids"]);
        var kids = Assert.Single(revealed, r => r.Name == "Kids");
        Assert.True(kids.IsHidden);
    }

    [Theory]
    [InlineData(CategorySort.ProviderOrder, new[] { "Sports", "UK", "Kids" })]
    [InlineData(CategorySort.Name, new[] { "Kids", "Sports", "UK" })]
    [InlineData(CategorySort.Size, new[] { "UK", "Sports", "Kids" })]
    public void RealCategoriesSortIndependentlyOfThePseudoRows(
        CategorySort sort, string[] expected)
    {
        var rail = Build(sort);

        // Pseudo-categories stay pinned at the top regardless of sort.
        Assert.Equal("All", rail[0].Name);
        Assert.Equal(expected, rail.Skip(1).Select(r => r.Name));
    }

    [Fact]
    public void OnlyRealCategoriesCanBeHidden()
    {
        var rail = Build(favorites: 3, recent: 2);

        Assert.False(rail.Single(r => r.Name == "All").CanHide);
        Assert.False(rail.Single(r => r.Name == "Favorites").CanHide);
        Assert.False(rail.Single(r => r.Name == "Recent").CanHide);
        Assert.True(rail.Single(r => r.Name == "Sports").CanHide);
    }

    [Fact]
    public void PseudoCategoriesHaveStablePersistKeys()
    {
        Assert.Equal("@all", CategorySelection.All.Instance.PersistKey);
        Assert.Equal("@favorites", CategorySelection.Favorites.Instance.PersistKey);
        Assert.Equal("@recent", CategorySelection.Recent.Instance.PersistKey);
        Assert.Equal("sports", new CategorySelection.Real("sports", "Sports", 0).PersistKey);
    }
}

/// <summary>
/// IPTV catalogs are full of numbered names, and plain alphabetical ordering
/// makes a channel list look broken.
/// </summary>
public sealed class NaturalComparerTests
{
    private static int Compare(string a, string b) => Math.Sign(NaturalComparer.Instance.Compare(a, b));

    [Theory]
    // The case this exists for.
    [InlineData("TSN 2", "TSN 10", -1)]
    [InlineData("TSN 10", "TSN 2", 1)]
    [InlineData("Season 9", "Season 10", -1)]
    [InlineData("Episode 100", "Episode 99", 1)]
    // Ordinary alphabetical still works.
    [InlineData("BBC One", "Cartoon Network", -1)]
    [InlineData("apple", "Banana", -1)]
    [InlineData("TSN 1", "TSN 1", 0)]
    // Case is ignored.
    [InlineData("tsn 2", "TSN 10", -1)]
    // Digits compare by value, not by digit count alone.
    [InlineData("Ch 007", "Ch 7", 1)]
    [InlineData("Ch 08", "Ch 9", -1)]
    // A prefix sorts before a longer string.
    [InlineData("TSN", "TSN 1", -1)]
    public void ComparesNumbersByValue(string a, string b, int expected) =>
        Assert.Equal(expected, Compare(a, b));

    [Fact]
    public void SortsAChannelListTheWayAHumanWould()
    {
        var channels = new List<string>
        {
            "TSN 10", "TSN 2", "TSN 1", "Sportsnet 360", "TSN 5", "BBC One",
        };

        channels.Sort(NaturalComparer.Instance);

        Assert.Equal(
            ["BBC One", "Sportsnet 360", "TSN 1", "TSN 2", "TSN 5", "TSN 10"],
            channels);
    }

    [Fact]
    public void HandlesNullsAndAbsurdlyLongDigitRuns()
    {
        Assert.Equal(0, Compare2(null, null));
        Assert.Equal(-1, Compare2(null, "x"));
        Assert.Equal(1, Compare2("x", null));

        // Long enough to overflow any integer parse.
        var huge = new string('9', 400);
        Assert.Equal(-1, Compare("Item " + new string('9', 399), "Item " + huge));

        static int Compare2(string? a, string? b) =>
            Math.Sign(NaturalComparer.Instance.Compare(a, b));
    }
}
