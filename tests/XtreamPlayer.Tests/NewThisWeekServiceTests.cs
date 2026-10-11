using Microsoft.Extensions.Logging.Abstractions;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Infrastructure.TvMaze;
using XtreamPlayer.Infrastructure.Xtream;
using Xunit;

namespace XtreamPlayer.Tests;

public sealed class NewThisWeekServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "xp-ntw-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(Start);
    private readonly FakeCatalog _catalog = new();
    private readonly FakeSchedule _schedule = new();

    public NewThisWeekServiceTests()
    {
        _catalog.SetSeries(
            Series("doc", "Doc (2025)"),
            Series("gold", "Gold Rush"),
            Series("other", "Not On TV"));

        _schedule.Current =
        [
            new AiredEpisode(1, 1, "Doc", 2025, 3, 2, "Ep", Start - TimeSpan.FromDays(2)),
            new AiredEpisode(2, 2, "Gold Rush", 2010, 17, 1, "Ep", Start - TimeSpan.FromDays(2)),
        ];

        _catalog.Episodes["doc"] = [Episode(3, 1, 9), Episode(3, 2, 1.5)];

        // The provider's newest is S14E1; the S17E1 that aired is not there.
        _catalog.Episodes["gold"] = [Episode(14, 1, 120)];
    }

    private NewThisWeekService Create() =>
        new(_catalog, _schedule, NullLogger<NewThisWeekService>.Instance, _directory, _clock);

    private static SeriesItem Series(string id, string name, DateTimeOffset? modified = null) => new()
    {
        Id = id,
        Name = name,
        CategoryId = "c",
        LastModified = modified ?? Start - TimeSpan.FromDays(1),
    };

    private static SeriesEpisode Episode(int season, int number, double addedDaysAgo) => new()
    {
        Id = $"{season}x{number}",
        Title = "Episode",
        SeasonNumber = season,
        EpisodeNumber = number,
        StreamUrl = "http://example.invalid/e",
        Added = Start - TimeSpan.FromDays(addedDaysAgo),
    };

    [Fact]
    public async Task OnlySeriesWhoseAiredEpisodeIsThereAreShown()
    {
        var service = Create();
        await service.EnsureLoadedAsync();
        await service.RefreshAsync();

        Assert.Equal(["doc"], service.SeriesIds);

        // Only the two title matches cost a request.
        Assert.Equal(["doc", "gold"], _catalog.Requested.Order());
    }

    [Fact]
    public async Task CachedResultsShowWithoutAnyRequests()
    {
        var first = Create();
        await first.EnsureLoadedAsync();
        await first.RefreshAsync();
        _catalog.Requested.Clear();

        var second = Create();
        await second.EnsureLoadedAsync();
        Assert.Equal(["doc"], second.SeriesIds);

        // Found stays found, and a fresh miss is not asked about again yet.
        await second.RefreshAsync();
        Assert.Empty(_catalog.Requested);
    }

    [Fact]
    public async Task AMissIsRecheckedLaterAndShowsOnceTheEpisodeArrives()
    {
        var service = Create();
        await service.EnsureLoadedAsync();
        await service.RefreshAsync();
        _catalog.Requested.Clear();

        _catalog.Episodes["gold"] = [Episode(14, 1, 120), Episode(17, 1, 0)];
        _clock.Now += TimeSpan.FromHours(7);
        await service.RefreshAsync();

        Assert.Equal(["gold"], _catalog.Requested);
        Assert.Equal(["doc", "gold"], service.SeriesIds.Order());
    }

    [Fact]
    public async Task AMissIsRecheckedAsSoonAsTheCatalogChangesTheSeries()
    {
        var service = Create();
        await service.EnsureLoadedAsync();
        await service.RefreshAsync();
        _catalog.Requested.Clear();

        _catalog.SetSeries(
            Series("doc", "Doc (2025)"),
            Series("gold", "Gold Rush", Start),
            Series("other", "Not On TV"));

        await service.RefreshAsync();

        Assert.Equal(["gold"], _catalog.Requested);
    }

    /// <summary>A provider that is down must not be asked about every series.</summary>
    [Fact]
    public async Task RepeatedFailuresStopTheRun()
    {
        _catalog.SetSeries([.. Enumerable.Range(0, 10).Select(i => Series($"s{i}", $"Show {i}"))]);
        _schedule.Current =
        [
            .. Enumerable.Range(0, 10).Select(i =>
                new AiredEpisode(i, i, $"Show {i}", null, 1, 1, "Ep", Start - TimeSpan.FromDays(1))),
        ];
        _catalog.Fail = true;

        var service = Create();
        await service.EnsureLoadedAsync();
        await service.RefreshAsync();

        Assert.Equal(3, _catalog.Requested.Count);
        Assert.Empty(service.SeriesIds);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeSchedule : IAiringScheduleService
    {
        public IReadOnlyList<AiredEpisode> Current { get; set; } = [];

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public Task EnsureLoadedAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task RefreshIfStaleAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeCatalog : ICatalogStore
    {
        public CatalogState Current { get; private set; } = new();

        public Dictionary<string, List<SeriesEpisode>> Episodes { get; } = new(StringComparer.Ordinal);

        public List<string> Requested { get; } = [];

        public bool Fail { get; set; }

        public event EventHandler<CatalogState>? Changed
        {
            add { }
            remove { }
        }

        public event EventHandler<CatalogProgress>? Progress
        {
            add { }
            remove { }
        }

        public void SetSeries(params SeriesItem[] items) =>
            Current = new CatalogState
            {
                ProfileId = "p1",
                Series = new Catalog<SeriesItem> { Items = items, Categories = [] },
            };

        public Task<SeriesDetail?> GetSeriesDetailAsync(string seriesId, CancellationToken ct = default)
        {
            Requested.Add(seriesId);

            if (Fail)
            {
                throw new InvalidOperationException("Provider is down.");
            }

            var episodes = Episodes.GetValueOrDefault(seriesId) ?? [];

            return Task.FromResult<SeriesDetail?>(new SeriesDetail
            {
                SeriesId = seriesId,
                Name = seriesId,
                EpisodesBySeason = episodes
                    .GroupBy(e => e.SeasonNumber)
                    .ToDictionary(g => g.Key, g => (IReadOnlyList<SeriesEpisode>)g.ToList()),
            });
        }

        public Task LoadAsync(Profile profile, bool forceRefresh = false, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<VodDetail?> GetVodDetailAsync(string vodId, CancellationToken ct = default) =>
            Task.FromResult<VodDetail?>(null);

        public void Clear()
        {
        }
    }
}
