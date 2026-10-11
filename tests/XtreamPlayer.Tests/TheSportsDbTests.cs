using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using XtreamPlayer.Core.Abstractions;
using XtreamPlayer.Core.Library;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Infrastructure.Security;
using XtreamPlayer.Infrastructure.Sports;
using Xunit;

namespace XtreamPlayer.Tests;

/// <summary>
/// Rows trimmed from real <c>eventsday.php</c> responses for 2 and 6 October 2026.
/// </summary>
public sealed class TheSportsDbParserTests
{
    internal const string Day = """
        {"events":[
          {"idEvent":"2475437","strTimestamp":"2026-10-06T00:15:00","strEvent":"New Orleans Saints vs Atlanta Falcons",
           "strLeague":"NFL","strHomeTeam":"New Orleans Saints","strAwayTeam":"Atlanta Falcons",
           "dateEvent":"2026-10-06","strTime":"00:15:00"},
          {"idEvent":"2458019","strTimestamp":"2026-10-02T13:30:00","strEvent":"ONE Friday Fights 173 Tonglampoon vs Tobias",
           "strLeague":"ONE","strHomeTeam":null,"strAwayTeam":null},
          {"idEvent":"1","strTimestamp":"2026-10-06T00:00:00","strEvent":"Arctic Open 2026","strLeague":"BWF World Tour"},
          {"idEvent":"2","strTimestamp":null,"dateEvent":"2026-10-06","strTime":"09:00:00",
           "strEvent":"Hanshin Tigers vs Hiroshima Toyo Carp","strLeague":"Nippon Baseball League",
           "strHomeTeam":"Hanshin Tigers","strAwayTeam":"Hiroshima Toyo Carp"}
        ]}
        """;

    [Fact]
    public void ReadsTeamsWithTheirShortNames()
    {
        var game = TheSportsDbClient.Parse(Day)[0];

        Assert.Equal("thesportsdb: NFL", game.League);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 0, 15, 0, TimeSpan.Zero), game.Start);
        Assert.Equal(["New Orleans Saints", "Saints"], game.Sides[0].Names);
        Assert.Equal(["Atlanta Falcons", "Falcons"], game.Sides[1].Names);
    }

    [Fact]
    public void ReadsAFightFromTheEventNameAndDropsTheCardName()
    {
        var fight = TheSportsDbClient.Parse(Day)[1];

        Assert.Equal(["Tonglampoon"], fight.Sides[0].Names);
        Assert.Equal(["Tobias"], fight.Sides[1].Names);
    }

    [Fact]
    public void SkipsEventsWithNoSidesAndReadsOlderRowsByDateAndTime()
    {
        var games = TheSportsDbClient.Parse(Day);

        Assert.Equal(3, games.Count);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero), games[2].Start);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"events":null}""")]
    public void AnEmptyDayHasNoGames(string json) => Assert.Empty(TheSportsDbClient.Parse(json));

    [Fact]
    public async Task ARefusedKeyIsRejectedButDoesNotFailTheFeed()
    {
        var factory = new Factory(HttpStatusCode.BadRequest,
            """{"Message":"Invalid Premium API key: Signup here: https:\/\/www.thesportsdb.com\/pricing"}""");

        await Assert.ThrowsAsync<SportsDbKeyRejectedException>(() =>
            new TheSportsDbClient(factory.CreateClient("")).GetDayAsync("nope", new DateOnly(2026, 10, 6)));

        Assert.Empty(await new TheSportsDbFeed("nope").GetDayAsync(factory, new DateOnly(2026, 10, 6)));
    }

    [Fact]
    public void TheFeedNeverPrintsItsKey() =>
        Assert.DoesNotContain("Sekr3t", new TheSportsDbFeed("Sekr3t").ToString(), StringComparison.Ordinal);

    [Fact]
    public void TheGuideCreditsTheSource()
    {
        var matcher = new SportsEventMatcher(TheSportsDbClient.Parse(Day));
        var channel = new LiveChannel
        {
            Id = "ch",
            Name = "ONE 03: ONE Friday Fights: Tonglampoon vs Tobias (2026-10-02 09:30:00)",
            CategoryId = "c",
            StreamUrl = "http://example/ch",
        };

        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var programme = Assert.Single(
            ChannelNameSchedule.Programmes(channel, now.AddHours(-2), now.AddHours(28), now, matcher));

        Assert.Equal(new DateTimeOffset(2026, 10, 2, 13, 30, 0, TimeSpan.Zero), programme.Start);
        Assert.StartsWith("Time from TheSportsDB.", programme.Description);
    }

    internal sealed class Factory(HttpStatusCode status, string body) : IHttpClientFactory
    {
        public int Calls { get; private set; }

        public HttpClient CreateClient(string name) =>
            new(new Handler(this, status, body), disposeHandler: true) { BaseAddress = new Uri("https://www.thesportsdb.com/") };

        private sealed class Handler(Factory owner, HttpStatusCode status, string body) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Calls++;
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
            }
        }
    }
}

public sealed class SportsDbKeyStoreTests
{
    private readonly MemorySettings _settings = new();

    private SportsDbKeyStore Create() => new(
        _settings,
        new CredentialProtector(),
        new TheSportsDbParserTests.Factory(HttpStatusCode.OK, TheSportsDbParserTests.Day));

    [Fact]
    public async Task KeepsTheKeyProtectedAndReadsItBack()
    {
        await Create().SetAsync("  Ab12Cd34  ");

        Assert.Equal("Ab12Cd34", await Create().GetAsync());

        var stored = _settings.Values[SettingKeys.SportsDbApiKey]!;
        Assert.StartsWith(new CredentialProtector().Scheme.ToStorageString() + ":", stored);

        if (OperatingSystem.IsWindows())
        {
            Assert.DoesNotContain("Ab12Cd34", stored, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankKeyRemovesIt(string? key)
    {
        await Create().SetAsync("Ab12Cd34");
        await Create().SetAsync(key);

        Assert.Null(await Create().GetAsync());
        Assert.False(_settings.Values.ContainsKey(SettingKeys.SportsDbApiKey));
    }

    [Fact]
    public async Task AnUnreadableValueIsNoKey()
    {
        _settings.Values[SettingKeys.SportsDbApiKey] = "dpapi-user:not base64!";

        Assert.Null(await Create().GetAsync());
    }

    [Fact]
    public async Task TestingAKeyCountsTodaysEvents() =>
        Assert.Equal(3, await Create().TestAsync("Ab12Cd34"));

    [Fact]
    public async Task TestingARefusedKeyIsNull()
    {
        var store = new SportsDbKeyStore(
            _settings, new CredentialProtector(),
            new TheSportsDbParserTests.Factory(HttpStatusCode.BadRequest, """{"Message":"Invalid Premium API key"}"""));

        Assert.Null(await store.TestAsync("nope"));
    }

    private sealed class MemorySettings : ISettingsRepository
    {
        public Dictionary<string, string?> Values { get; } = [];

        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(Values.GetValueOrDefault(key));

        public Task SetAsync(string key, string? value, CancellationToken ct = default)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, string?>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string?>>(Values);

        public Task RemoveAsync(string key, CancellationToken ct = default)
        {
            Values.Remove(key);
            return Task.CompletedTask;
        }
    }
}

public sealed class SportsScheduleWithKeyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "xp-tsdb-" + Guid.NewGuid().ToString("N"));
    private readonly TheSportsDbParserTests.Factory _factory = new(HttpStatusCode.OK, TheSportsDbParserTests.Day);
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 6, 16, 0, 0, TimeSpan.Zero));
    private string? _key;

    private SportsScheduleService Create() => new(
        _factory,
        NullLogger<SportsScheduleService>.Instance,
        Path.Combine(_directory, "schedule.json.gz"),
        _clock,
        [],
        _ => Task.FromResult(_key));

    [Fact]
    public async Task FetchesTheSportsDbOnlyWithAKey()
    {
        await Create().RefreshIfStaleAsync();
        Assert.Equal(0, _factory.Calls);

        _key = "Ab12Cd34";
        var service = Create();
        await service.RefreshIfStaleAsync();

        // Yesterday to two days out, one request each.
        Assert.Equal(4, _factory.Calls);
        Assert.NotNull(service.Matcher.Find("Saints vs Falcons", new DateTimeOffset(2026, 10, 6, 0, 15, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task RemovingTheKeyDropsItsGames()
    {
        _key = "Ab12Cd34";
        var service = Create();
        await service.RefreshIfStaleAsync();
        Assert.True(service.Matcher.Count > 0);

        _key = null;
        await service.RefreshIfStaleAsync();

        Assert.Equal(0, service.Matcher.Count);
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
        public override DateTimeOffset GetUtcNow() => now;
    }
}
