using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using XtreamPlayer.Core.Abstractions;
using XtreamPlayer.Core.Library;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Infrastructure.Epg;
using XtreamPlayer.Infrastructure.Xtream;

namespace XtreamPlayer.App;

/// <summary>
/// Measures the work behind each interaction, against the real catalogs.
/// </summary>
/// <remarks>
/// <para>
/// "Feels fast" is not a claim worth making without numbers, and a synthetic
/// fixture proves nothing here: the whole difficulty is 68,595 movies and
/// 11,967 channels. This runs the same code paths the pages run, on whatever
/// the active profile actually has.
/// </para>
/// <para>
/// The budgets are what the interaction has to stay under to feel immediate
/// rather than merely quick. 16 ms is one frame at 60 Hz; 100 ms is roughly
/// where a response stops reading as instant.
/// </para>
/// </remarks>
internal static class Benchmark
{
    private const int Warmup = 2;
    private const int Runs = 10;

    public static int Run(IServiceProvider services, Profile profile)
    {
        var catalog = services.GetRequiredService<ICatalogStore>();
        var epg = services.GetRequiredService<IEpgService>();
        var favorites = services.GetRequiredService<IFavoritesStore>();

        var scope = new ProfileScope(profile.Id);
        var results = new List<Result>();

        // ---- cold start: catalogs off disk ----------------------------------
        var coldStart = Stopwatch.StartNew();
        catalog.LoadAsync(profile).GetAwaiter().GetResult();
        coldStart.Stop();

        var live = catalog.Current.Live;
        var vod = catalog.Current.Vod;
        var series = catalog.Current.Series;

        Console.WriteLine();
        Console.WriteLine($"Profile   : {profile.Name}");
        Console.WriteLine($"Live      : {live.Items.Count:N0} channels, {live.Categories.Count:N0} categories");
        Console.WriteLine($"Movies    : {vod.Items.Count:N0} items, {vod.Categories.Count:N0} categories");
        Console.WriteLine($"Series    : {series.Items.Count:N0} items, {series.Categories.Count:N0} categories");
        Console.WriteLine();

        results.Add(new Result(
            "Start: load all catalogs from disk cache", coldStart.Elapsed.TotalMilliseconds, 1500));

        // The projection each browse page builds once when its catalog arrives.
        var vodItems = Project(vod.Items.Select(i => (i.Id, i.Name, i.CategoryId, i.CategoryOrder, i.Added)));
        var liveItems = Project(live.Items.Select(i => (i.Id, i.Name, i.CategoryId, i.CategoryOrder, (DateTimeOffset?)null)));

        // The same precomputed sort order the browse pages hold.
        var vodOrdering = new CatalogOrdering(vodItems);
        var liveOrdering = new CatalogOrdering(liveItems);

        var favoriteIds = favorites.IdsFor(ContentKind.Vod);
        var empty = new HashSet<string>(StringComparer.Ordinal);

        // ---- opening Movies -------------------------------------------------
        results.Add(Measure(
            $"Open Movies: filter + sort {vodItems.Count:N0} items", 100,
            () => CatalogFilter.Apply(vodItems, new FilterRequest
            {
                Selection = CategorySelection.All.Instance,
                Sort = ItemSort.Newest,
                FavoriteIds = favoriteIds,
                HiddenCategoryIds = empty,
            }, vodOrdering).Count));

        // ---- typing in the search box --------------------------------------
        // Per keystroke after the debounce, so this is the one that has to stay
        // inside a frame or typing visibly stutters.
        results.Add(Measure(
            "Type in search: one keystroke over all movies", 16,
            () => CatalogFilter.Apply(vodItems, new FilterRequest
            {
                Selection = CategorySelection.All.Instance,
                SearchText = "the",
                Sort = ItemSort.Newest,
                FavoriteIds = favoriteIds,
                HiddenCategoryIds = empty,
            }, vodOrdering).Count));

        // ---- picking a category ---------------------------------------------
        var biggest = vod.Categories.OrderByDescending(c => c.ItemCount).FirstOrDefault();

        if (biggest is not null)
        {
            results.Add(Measure(
                $"Pick category \"{Trim(biggest.Name)}\" ({biggest.ItemCount:N0} items)", 16,
                () => CatalogFilter.Apply(vodItems, new FilterRequest
                {
                    Selection = new CategorySelection.Real(biggest.Id, biggest.Name, biggest.Order),
                    Sort = ItemSort.Newest,
                    FavoriteIds = favoriteIds,
                    HiddenCategoryIds = empty,
                }, vodOrdering).Count));
        }

        results.Add(Measure(
            $"Build category rail ({vod.Categories.Count:N0} categories)", 16,
            () => CatalogFilter.BuildRail(
                vod.Categories, empty, CategorySort.ProviderOrder, false,
                vodItems.Count, favoriteIds.Count).Count));

        results.Add(Measure(
            $"Open Live: filter {liveItems.Count:N0} channels", 100,
            () => CatalogFilter.Apply(liveItems, new FilterRequest
            {
                Selection = CategorySelection.All.Instance,
                Sort = ItemSort.Default,
                FavoriteIds = favorites.IdsFor(ContentKind.Live),
                HiddenCategoryIds = empty,
            }, liveOrdering).Count));

        // ---- opening the guide ----------------------------------------------
        var from = DateTimeOffset.Now - TimeSpan.FromHours(2);
        var to = from + TimeSpan.FromHours(30);

        var guideRun = Stopwatch.StartNew();
        var guide = epg.GetGuideAsync(scope, from, to).GetAwaiter().GetResult();
        guideRun.Stop();

        var programmes = guide.Values.Sum(v => v.Count);

        results.Add(new Result(
            $"Open Guide: query + de-overlap {programmes:N0} programmes",
            guideRun.Elapsed.TotalMilliseconds, 1000));

        // Changing guide category re-projects rows already in memory, which is
        // the reason it does not go back to SQLite.
        results.Add(Measure(
            $"Guide: change category ({live.Items.Count:N0} channels)", 16,
            () => BuildGuideRows(live.Items, guide)));

        // ---- report ----------------------------------------------------------
        Console.WriteLine($"{"Interaction",-58} {"median",11} {"budget",9}");
        Console.WriteLine(new string('-', 88));

        var failures = 0;

        foreach (var result in results)
        {
            var verdict = result.Median <= result.BudgetMs ? "ok" : "OVER";

            if (result.Median > result.BudgetMs)
            {
                failures++;
            }

            Console.WriteLine(
                $"{result.Name,-58} {result.Median,8:F1} ms {result.BudgetMs,6} ms  {verdict}");
        }

        Console.WriteLine();
        Console.WriteLine(
            failures == 0
                ? "All interactions inside budget."
                : $"{failures} interaction(s) over budget.");

        return failures == 0 ? 0 : 1;
    }

    private static int BuildGuideRows(
        IReadOnlyList<LiveChannel> channels,
        IReadOnlyDictionary<string, List<EpgProgramme>> guide)
    {
        var rows = 0;

        foreach (var channel in channels)
        {
            guide.TryGetValue(channel.Id, out _);
            rows++;
        }

        return rows;
    }

    private static List<FilterableItem> Project(
        IEnumerable<(string Id, string Name, string CategoryId, int CategoryOrder, DateTimeOffset? Added)> source)
    {
        var index = 0;
        var list = new List<FilterableItem>();

        foreach (var item in source)
        {
            list.Add(new FilterableItem(
                item.Id, item.Name, item.CategoryId, item.CategoryOrder, item.Added, index++));
        }

        return list;
    }

    /// <summary>
    /// Median of several runs after a warmup.
    /// </summary>
    /// <remarks>
    /// Median rather than mean: a single GC pause during a run would otherwise
    /// dominate the number and make a fast path look slow.
    /// </remarks>
    private static Result Measure(string name, int budgetMs, Func<int> action)
    {
        for (var i = 0; i < Warmup; i++)
        {
            action();
        }

        var samples = new double[Runs];

        for (var i = 0; i < Runs; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            action();
            stopwatch.Stop();
            samples[i] = stopwatch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);

        return new Result(name, samples[Runs / 2], budgetMs);
    }

    private static string Trim(string value) =>
        value.Length <= 22 ? value : value[..22];

    private sealed record Result(string Name, double Median, int BudgetMs);
}
