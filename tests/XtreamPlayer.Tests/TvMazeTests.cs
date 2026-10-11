using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using XtreamPlayer.Infrastructure.TvMaze;
using Xunit;

namespace XtreamPlayer.Tests;

/// <summary>
/// Fixtures trimmed from real <c>/schedule</c> and <c>/schedule/web</c>
/// responses.
/// </summary>
public sealed class TvMazeParserTests
{
    private const string Broadcast = """
        [
          {"id":3779766,"name":"Meat Is Murder","season":1,"number":2,"airdate":"2026-10-03",
           "airtime":"00:31","airstamp":"2026-10-04T04:31:00+00:00",
           "show":{"id":77795,"name":"Get Jiro","premiered":"2026-10-03","language":"English",
                   "network":{"id":10,"name":"Adult Swim"}}},
          {"id":1,"name":"No time yet","season":1,"number":1,"airstamp":null,
           "show":{"id":2,"name":"Unscheduled","premiered":null}},
          {"id":2,"name":"Spanish","season":3,"number":4,"airstamp":"2026-10-04T01:00:00+00:00",
           "show":{"id":3,"name":"Telenovela","premiered":null,"language":"Spanish"}}
        ]
        """;

    private const string Web = """
        [
          {"id":10,"name":"Episode 5","season":2,"number":5,"airstamp":"2026-10-04T07:00:00+00:00",
           "_embedded":{"show":{"id":20,"name":"The Great Canadian Baking Show","premiered":"2017-11-01",
                                "language":"English","webChannel":{"name":"CBC Gem"}}}},
          {"id":11,"name":"第5集","season":1,"number":5,"airstamp":"2026-10-04T04:00:00+00:00",
           "_embedded":{"show":{"id":21,"name":"某剧","premiered":"2026-09-20","language":"Chinese"}}}
        ]
        """;

    [Fact]
    public void ReadsTheBroadcastShape()
    {
        var episodes = TvMazeClient.ParseSchedule(Broadcast, web: false);

        var jiro = episodes[0];
        Assert.Equal(3779766, jiro.EpisodeId);
        Assert.Equal(77795, jiro.ShowId);
        Assert.Equal("Get Jiro", jiro.ShowName);
        Assert.Equal(2026, jiro.PremieredYear);
        Assert.Equal((1, 2), (jiro.Season, jiro.Number));
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 4, 31, 0, TimeSpan.Zero), jiro.AiredAt);

        // No air time means no way to tell whether it is out, so it is dropped.
        // The broadcast feed is already US-only, so language is not filtered.
        Assert.Equal(["Get Jiro", "Telenovela"], episodes.Select(e => e.ShowName));
        Assert.Null(episodes[1].PremieredYear);
    }

    [Fact]
    public void ReadsTheWebShapeAndKeepsEnglishShowsOnly()
    {
        var episode = Assert.Single(TvMazeClient.ParseSchedule(Web, web: true));

        Assert.Equal("The Great Canadian Baking Show", episode.ShowName);
        Assert.Equal(2017, episode.PremieredYear);
        Assert.Equal(20, episode.ShowId);
    }

    [Fact]
    public void AnEmptyScheduleIsHandled()
    {
        Assert.Empty(TvMazeClient.ParseSchedule("[]", web: false));
        Assert.Empty(TvMazeClient.ParseSchedule("null", web: true));
    }
}

public sealed class AiringScheduleServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "xp-tvmaze-" + Guid.NewGuid().ToString("N"));
    private readonly CountingHandler _handler = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));

    private string CachePath => Path.Combine(_directory, "schedule.json.gz");

    private AiringScheduleService Create() =>
        new(new StubFactory(_handler), NullLogger<AiringScheduleService>.Instance, CachePath, _clock);

    [Fact]
    public async Task FetchesTheWeekOnceThenOnlyTheRecentDays()
    {
        await Create().RefreshIfStaleAsync();

        // Eight days, two feeds each.
        Assert.Equal(16, _handler.Calls);
        Assert.True(File.Exists(CachePath));

        // A fresh instance reads the cache instead of the network.
        var second = Create();
        await second.RefreshIfStaleAsync();
        Assert.Equal(16, _handler.Calls);
        Assert.Single(second.Current);

        // Later, only today and yesterday are worth refetching.
        _clock.Now += TimeSpan.FromHours(7);
        await second.RefreshIfStaleAsync();
        Assert.Equal(20, _handler.Calls);
    }

    /// <summary>Offline must not cost a timeout per day, or lose the cache.</summary>
    [Fact]
    public async Task AFailureStopsTheRunAndKeepsTheLastGoodData()
    {
        var service = Create();
        await service.RefreshIfStaleAsync();

        _handler.Fail = true;
        _clock.Now += TimeSpan.FromHours(7);
        await service.RefreshIfStaleAsync();

        Assert.Equal(17, _handler.Calls);
        Assert.Single(service.Current);
    }

    [Fact]
    public async Task EnsureLoadedReadsTheCacheWithoutFetching()
    {
        await Create().RefreshIfStaleAsync();
        var calls = _handler.Calls;

        var service = Create();
        await service.EnsureLoadedAsync();

        Assert.Equal(calls, _handler.Calls);
        Assert.Single(service.Current);
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

    /// <summary>The same one episode for every day; de-duplication leaves one.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public bool Fail { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;

            if (Fail)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            const string json = """
                [{"id":1,"name":"Pilot","season":1,"number":1,"airstamp":"2026-10-04T01:00:00+00:00",
                  "show":{"id":1,"name":"Doc","premiered":"2025-01-07","language":"English"}}]
                """;

            var body = request.RequestUri!.AbsolutePath.Contains("/web", StringComparison.Ordinal) ? "[]" : json;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.tvmaze.com/") };
    }
}
