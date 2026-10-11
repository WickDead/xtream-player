using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using XtreamPlayer.Core.Library;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Infrastructure.Sports;
using Xunit;

namespace XtreamPlayer.Tests;

/// <summary>
/// Fixtures trimmed from real ESPN scoreboard responses for 5 and 6 October 2026.
/// </summary>
public sealed class EspnParserTests
{
    internal const string Nhl = """
        {"events":[
          {"id":"401891815","date":"2026-10-06T23:00Z","name":"Carolina Hurricanes at Montreal Canadiens",
           "competitions":[{"date":"2026-10-06T23:00Z","competitors":[
             {"homeAway":"home","team":{"displayName":"Montreal Canadiens","shortDisplayName":"Canadiens","name":"Canadiens","location":"Montreal","abbreviation":"MTL"}},
             {"homeAway":"away","team":{"displayName":"Carolina Hurricanes","shortDisplayName":"Hurricanes","name":"Hurricanes","location":"Carolina","abbreviation":"CAR"}}]}]},
          {"id":"401891819","date":"2026-10-06T23:30Z","name":"New York Islanders at New York Rangers",
           "competitions":[{"date":"2026-10-06T23:30Z","competitors":[
             {"homeAway":"home","team":{"displayName":"New York Rangers","shortDisplayName":"Rangers","name":"Rangers","location":"New York","abbreviation":"NYR"}},
             {"homeAway":"away","team":{"displayName":"New York Islanders","shortDisplayName":"Islanders","name":"Islanders","location":"New York","abbreviation":"NYI"}}]}]}
        ]}
        """;

    internal const string Nfl = """
        {"events":[
          {"date":"2026-10-06T00:15Z","name":"Atlanta Falcons at New Orleans Saints",
           "competitions":[{"date":"2026-10-06T00:15Z","competitors":[
             {"team":{"displayName":"New Orleans Saints","shortDisplayName":"Saints","name":"Saints","abbreviation":"NO"}},
             {"team":{"displayName":"Atlanta Falcons","shortDisplayName":"Falcons","name":"Falcons","abbreviation":"ATL"}}]}]}
        ]}
        """;

    internal const string Ufc = """
        {"events":[
          {"date":"2026-10-03T20:00Z","name":"UFC 332: Silva vs. Wang",
           "competitions":[
             {"date":"2026-10-03T20:00Z","competitors":[
               {"athlete":{"displayName":"Court McGee","shortName":"C. McGee"}},
               {"athlete":{"displayName":"Eric Nolan","shortName":"E. Nolan"}}]},
             {"date":"2026-10-04T00:00Z","competitors":[
               {"athlete":{"displayName":"Natalia Silva","shortName":"N. Silva"}},
               {"athlete":{"displayName":"Wang Cong","shortName":"W. Cong"}}]}]}
        ]}
        """;

    [Fact]
    public void ReadsTeamsAndTheirNames()
    {
        var games = EspnClient.Parse("nhl", Nhl);

        Assert.Equal(2, games.Count);

        var game = games[0];
        Assert.Equal("nhl", game.League);
        Assert.Equal("Carolina Hurricanes at Montreal Canadiens", game.Name);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 23, 0, 0, TimeSpan.Zero), game.Start);
        Assert.Equal(["Montreal Canadiens", "Canadiens", "MTL"], game.Sides[0].Names);
    }

    [Fact]
    public void ReadsEachBoutOfAFightCardWithItsOwnStart()
    {
        var bouts = EspnClient.Parse("ufc", Ufc);

        Assert.Equal(2, bouts.Count);
        Assert.Equal("Natalia Silva vs Wang Cong", bouts[1].Name);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero), bouts[1].Start);

        // Fight channels name surnames, and "Wang Cong" is family name first.
        Assert.Equal(["Natalia Silva", "Silva", "Natalia"], bouts[1].Sides[0].Names);
        Assert.Equal(["Wang Cong", "Cong", "Wang"], bouts[1].Sides[1].Names);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"events":[]}""")]
    [InlineData("""{"events":[{"name":"No competitions"}]}""")]
    public void AnEmptyOrOddScoreboardHasNoGames(string json) =>
        Assert.Empty(EspnClient.Parse("nhl", json));
}

/// <summary>
/// Tiles trimmed from DAZN's guide for 2 October 2026 (UK).
/// </summary>
public sealed class DaznParserTests
{
    internal const string Day = """
        {"Id":"en-gb-2026-10-02","Tiles":[
          {"Title":"Whittaker vs. Wallace: Weigh-In","Start":"2026-10-02T17:00:00Z","End":"2026-10-02T17:46:37Z",
           "EventStartTime":"2026-10-02T17:00:00Z","Sport":{"Title":"Boxing"},"Competition":{"Title":"Matchroom Boxing"},
           "Contestants":[{"Title":"Ben Whittaker"},{"Title":"Liam Wallace"}]},
          {"Title":"Faour vs. Sachbazjan: Full Event Replay","Start":"2026-10-02T17:00:00Z","End":"2026-10-02T21:59:41Z",
           "EventStartTime":"2026-10-02T17:00:00Z","Sport":{"Title":"Boxing"},"Competition":{"Title":"Primetime Boxing"},
           "Contestants":[{"Title":"Elias Faour"},{"Title":"Narek Sachbazjan"}]},
          {"Title":"Malaga vs. Andorra","Start":"2026-10-02T17:30:00Z","End":null,
           "EventStartTime":"2026-10-02T17:30:00Z","Sport":{"Title":"Basketball"},"Competition":{"Title":"Liga ACB"},
           "Contestants":[{"Title":"Unicaja"},{"Title":"Andorra"}]},
          {"Title":"Grand Prix of Shanghai - Pole Position","Start":"2026-10-02T08:00:00Z","End":"2026-10-02T09:00:00Z",
           "Sport":{"Title":"Powerboat Racing"},"Contestants":[]}
        ]}
        """;

    [Fact]
    public void ReadsFightsAndGamesButNotWeighInsOrRaces()
    {
        var games = DaznClient.Parse("dazn-gb", Day);

        Assert.Equal(["Faour vs. Sachbazjan", "Malaga vs. Andorra"], games.Select(g => g.Name));

        var fight = games[0];
        Assert.Equal("dazn-gb: Primetime Boxing", fight.League);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 17, 0, 0, TimeSpan.Zero), fight.Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 21, 59, 41, TimeSpan.Zero), fight.Stop);
        Assert.Equal(["Elias Faour", "Faour", "Elias"], fight.Sides[0].Names);

        // The title's brief name is kept alongside the sponsor's.
        Assert.Equal(["Unicaja", "Malaga"], games[1].Sides[0].Names);
        Assert.Null(games[1].Stop);
    }

    [Fact]
    public void TheGuideUsesTheBroadcastTimesForAFight()
    {
        var matcher = new SportsEventMatcher(DaznClient.Parse("dazn-gb", Day));
        var channel = new LiveChannel
        {
            Id = "ch",
            Name = "LIVE EVENT 03 - 1pm Faour v Sachbazjan",
            CategoryId = "c",
            StreamUrl = "http://example/ch",
        };

        var now = new DateTimeOffset(2026, 10, 2, 15, 0, 0, TimeSpan.Zero);

        var programme = Assert.Single(
            ChannelNameSchedule.Programmes(channel, now.AddHours(-2), now.AddHours(28), now, matcher));

        Assert.Equal(new DateTimeOffset(2026, 10, 2, 17, 0, 0, TimeSpan.Zero), programme.Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 21, 59, 41, TimeSpan.Zero), programme.Stop);
        Assert.Equal("Time from DAZN.", programme.Description);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"Tiles":[]}""")]
    public void AnEmptyGuideHasNoGames(string json) =>
        Assert.Empty(DaznClient.Parse("dazn-gb", json));
}

/// <summary>
/// Event titles as <see cref="ChannelNameSchedule"/> reads them from real
/// channel names, against the real schedule for the same days.
/// </summary>
public sealed class SportsEventMatcherTests
{
    private static readonly DateTimeOffset Tuesday = new(2026, 10, 6, 22, 0, 0, TimeSpan.Zero);

    private static readonly SportsEventMatcher Matcher = new(
    [
        .. EspnClient.Parse("nhl", EspnParserTests.Nhl),
        .. EspnClient.Parse("nfl", EspnParserTests.Nfl),
        .. EspnClient.Parse("ufc", EspnParserTests.Ufc),
        Game("nba", "Brooklyn Nets at Minnesota Timberwolves", Tuesday.AddHours(2),
            ["Minnesota Timberwolves", "Timberwolves", "MIN"], ["Brooklyn Nets", "Nets", "BKN"]),
        Game("mls", "Vancouver Whitecaps at Chicago Fire FC", new DateTimeOffset(2026, 10, 7, 0, 30, 0, TimeSpan.Zero),
            ["Chicago Fire FC", "Chicago", "Fire", "CHI"], ["Vancouver Whitecaps", "Vancouver", "Whitecaps", "VAN"]),
        Game("epl", "Leeds United at Arsenal", new DateTimeOffset(2026, 10, 10, 11, 30, 0, TimeSpan.Zero),
            ["Arsenal", "ARS"], ["Leeds United", "Leeds", "LEE"]),
    ]);

    private static ScheduledGame Game(string league, string name, DateTimeOffset start, string[] home, string[] away) =>
        new(league, name, start, [new GameSide(home), new GameSide(away)]);

    [Theory]
    [InlineData("Islanders at Rangers", "New York Islanders at New York Rangers")]
    [InlineData("Rangers vs Islanders", "New York Islanders at New York Rangers")]
    [InlineData("NHL Carolina Hurricanes vs Montreal Canadiens", "Carolina Hurricanes at Montreal Canadiens")]
    [InlineData("Monday Night Football with Peyton and Eli: Falcons vs. Saints", "Atlanta Falcons at New Orleans Saints")]
    [InlineData("Falcons @ Saints", "Atlanta Falcons at New Orleans Saints")]
    [InlineData("Chicago vs. Vancouver (Spanish)", "Vancouver Whitecaps at Chicago Fire FC")]
    [InlineData("Performance View: BKN vs. MIN", "Brooklyn Nets at Minnesota Timberwolves")]
    public void FindsTheGameATitleNames(string title, string game) =>
        Assert.Equal(game, Matcher.Find(title, Tuesday)?.Name);

    [Fact]
    public void FindsTheBoutAFightTitleNames() =>
        Assert.Equal(
            new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero),
            Matcher.Find("Silva v Wang", new DateTimeOffset(2026, 10, 3, 22, 0, 0, TimeSpan.Zero))?.Start);

    [Theory]
    // Stale names from another night: one team plays, but not this game.
    [InlineData("Rangers at Red Wings")]
    [InlineData("Capitals at Hurricanes")]
    // One side is not enough.
    [InlineData("NHL Network: Rangers Preview")]
    // An abbreviation in a longer side is a word, not a team.
    [InlineData("Twins MIN vs Nets")]
    [InlineData("Faour v Sachbazjan")]
    [InlineData("")]
    public void LeavesTitlesThatNameNoKnownGame(string title) =>
        Assert.Null(Matcher.Find(title, Tuesday));

    [Fact]
    public void ADashSeparatesSidesWhenNothingElseDoes() =>
        Assert.Equal(
            "Leeds United at Arsenal",
            Matcher.Find("Premier League: Arsenal - Leeds United", new DateTimeOffset(2026, 10, 10, 14, 0, 0, TimeSpan.Zero))?.Name);

    /// <summary>Real false matches from replaying 2 October against the catalog.</summary>
    [Theory]
    [InlineData("Cal Poly Mustangs at Eastern Washington Eagles")]
    [InlineData("West Florida Argonauts at Southeast Missouri Redhawks")]
    [InlineData("Matchroom Boxing: Whittaker vs Wallace: Weigh-In")]
    public void ASideThatSaysMoreThanTheTeamIsAnotherTeam(string title)
    {
        var saturday = new DateTimeOffset(2026, 10, 3, 16, 0, 0, TimeSpan.Zero);
        var games = new SportsEventMatcher(
        [
            Game("ncaaf", "Boston College Eagles at SMU Mustangs", saturday,
                ["SMU Mustangs", "SMU", "Mustangs"], ["Boston College Eagles", "Boston College", "Eagles", "BC"]),
            Game("dazn", "Florida @ Missouri", saturday,
                ["Missouri Tigers", "Missouri"], ["Florida Gators", "Florida"]),
            Game("dazn", "Whittaker vs. Wallace", saturday,
                ["Ben Whittaker", "Whittaker", "Ben"], ["Liam Wallace", "Wallace", "Liam"]),
        ]);

        Assert.Null(games.Find(title, saturday));
    }

    [Theory]
    [InlineData("NCAAF Michigan State at Wisconsin")]
    [InlineData("#7 Alabama at #16 Mississippi State")]
    [InlineData("Brighton & Hove Albion v Crystal Palace Premier League Matchweek 7 2026/2027")]
    public void TagsRanksAndLeagueWordsAreNotPartOfASide(string title)
    {
        var saturday = new DateTimeOffset(2026, 10, 3, 16, 0, 0, TimeSpan.Zero);
        var games = new SportsEventMatcher(
        [
            Game("ncaaf", "Michigan State Spartans at Wisconsin Badgers", saturday,
                ["Wisconsin Badgers", "Wisconsin", "Badgers", "WIS"], ["Michigan State Spartans", "Michigan State", "Spartans", "MSU"]),
            Game("ncaaf", "Alabama Crimson Tide at Mississippi State Bulldogs", saturday,
                ["Mississippi State Bulldogs", "Mississippi State", "Bulldogs", "MSST"], ["Alabama Crimson Tide", "Alabama", "Crimson Tide", "ALA"]),
            Game("epl", "Crystal Palace at Brighton & Hove Albion", saturday,
                ["Brighton & Hove Albion", "Brighton", "BHA"], ["Crystal Palace", "CRY"]),
        ]);

        Assert.NotNull(games.Find(title, saturday));
    }

    [Fact]
    public void AGameTwoDaysAwayIsAnotherGame() =>
        Assert.Null(Matcher.Find("Islanders at Rangers", Tuesday.AddDays(2)));

    [Fact]
    public void ANameWithADateOnlyMovesByATimeZone()
    {
        // Women's soccer on ESPN+, and the same schools' football game the next
        // afternoon: a dated name must not jump a day to it.
        var football = new SportsEventMatcher(
        [
            Game("ncaaf", "Louisville Cardinals at NC State Wolfpack", new DateTimeOffset(2026, 10, 3, 19, 30, 0, TimeSpan.Zero),
                ["NC State Wolfpack", "NC State", "Wolfpack", "NCST"], ["Louisville Cardinals", "Louisville", "Cardinals", "LOU"]),
        ]);

        var channel = new LiveChannel
        {
            Id = "ch",
            Name = "US (ESPN+ 102) | Soccer: NC State vs. #17 Louisville (NC State vs. #17 Louisville) (2026-10-02 18:03:15)",
            CategoryId = "c",
            StreamUrl = "http://example/ch",
        };

        var now = new DateTimeOffset(2026, 10, 2, 20, 0, 0, TimeSpan.Zero);
        var programme = Assert.Single(
            ChannelNameSchedule.Programmes(channel, now.AddHours(-2), now.AddHours(28), now, football));

        Assert.Equal(new DateTimeOffset(2026, 10, 2, 22, 0, 0, TimeSpan.Zero), programme.Start);
        Assert.StartsWith("From the channel name.", programme.Description);
    }

    [Fact]
    public void ANameADayOutFindsTheRealDay()
    {
        // "NFL 16: Falcons @ Saints 01:15" reads as Tuesday 01:15 UK; the game
        // was Monday night in New Orleans.
        var game = Matcher.Find("Falcons @ Saints", Tuesday);

        Assert.Equal(new DateTimeOffset(2026, 10, 6, 0, 15, 0, TimeSpan.Zero), game?.Start);
    }

    [Fact]
    public void TheGuideUsesTheScheduledStart()
    {
        var channel = new LiveChannel
        {
            Id = "ch",
            Name = "UK (MAX 043) | Premier League: Arsenal _ Leeds United (2026-10-10 10:00:00)",
            CategoryId = "c",
            StreamUrl = "http://example/ch",
        };

        var from = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);

        var programme = Assert.Single(
            ChannelNameSchedule.Programmes(channel, from, from.AddDays(1), Tuesday, Matcher));

        Assert.Equal(new DateTimeOffset(2026, 10, 10, 11, 30, 0, TimeSpan.Zero), programme.Start);
        Assert.Equal(ChannelNameSchedule.DefaultEventLength, programme.Duration);
        Assert.Contains("ESPN", programme.Description);
    }

    [Theory]
    [InlineData("Saint Francis vs Case Western Reserve (Saint Francis vs CWRU)", "Saint Francis", "Case Western Reserve")]
    [InlineData("Hockey: Tri-City Storm vs Lincoln Stars (Home)", "Tri-City Storm", "Lincoln Stars")]
    [InlineData("Collingwood Magpies vs Carlton Blues | Melbourne Cricket Ground", "Collingwood Magpies", "Carlton Blues")]
    [InlineData("Phillies x Braves", "Phillies", "Braves")]
    public void SplitsATitleIntoItsSides(string title, string first, string second) =>
        Assert.Equal((first, second), SportsEventMatcher.SplitSides(title));
}

public sealed class SportsScheduleServiceTests : IDisposable
{
    private static readonly IReadOnlyList<ScheduleFeed> Leagues =
    [
        new EspnLeague("nhl", "hockey/nhl"),
        new EspnLeague("nfl", "football/nfl"),
        new EspnLeague("gone", "made/up"),
    ];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "xp-espn-" + Guid.NewGuid().ToString("N"));
    private readonly ScoreboardHandler _handler = new();

    // Tuesday noon in New York.
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 6, 16, 0, 0, TimeSpan.Zero));

    private string CachePath => Path.Combine(_directory, "espn.json.gz");

    private SportsScheduleService Create() =>
        new(new StubFactory(_handler), NullLogger<SportsScheduleService>.Instance, CachePath, _clock, Leagues);

    [Fact]
    public async Task FetchesEachLeagueDayOnceThenOnlyUpcomingDays()
    {
        await Create().RefreshIfStaleAsync();

        // Yesterday to two days out, three leagues. The league ESPN does not
        // know answers 400 and does not stop the others.
        Assert.Equal(12, _handler.Calls);
        Assert.True(File.Exists(CachePath));

        var second = Create();
        await second.RefreshIfStaleAsync();
        Assert.Equal(12, _handler.Calls);
        Assert.True(second.Matcher.Count > 0);

        // Later, yesterday is settled; today and later are rechecked.
        _clock.Now += TimeSpan.FromHours(4);
        await second.RefreshIfStaleAsync();
        Assert.Equal(21, _handler.Calls);
    }

    [Fact]
    public async Task AFailureStopsTheRunAndKeepsTheLastGoodData()
    {
        var service = Create();
        await service.RefreshIfStaleAsync();
        var known = service.Matcher.Count;

        _handler.Fail = true;
        _clock.Now += TimeSpan.FromHours(4);
        await service.RefreshIfStaleAsync();

        Assert.Equal(13, _handler.Calls);
        Assert.Equal(known, service.Matcher.Count);
    }

    [Fact]
    public async Task EnsureLoadedReadsTheCacheWithoutFetching()
    {
        await Create().RefreshIfStaleAsync();
        var calls = _handler.Calls;

        var service = Create();
        await service.EnsureLoadedAsync();

        Assert.Equal(calls, _handler.Calls);
        Assert.NotNull(service.Matcher.Find("Islanders at Rangers", _clock.Now));
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

    private sealed class ScoreboardHandler : HttpMessageHandler
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

            var path = request.RequestUri!.AbsolutePath;

            if (path.Contains("made/up", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
            }

            var body = path.Contains("hockey", StringComparison.Ordinal) ? EspnParserTests.Nhl : EspnParserTests.Nfl;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://site.api.espn.com/apis/site/v2/sports/") };
    }
}
