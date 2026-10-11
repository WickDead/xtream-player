using Xunit;
using XtreamPlayer.Core.Library;
using XtreamPlayer.Core.Models;

namespace XtreamPlayer.Tests;

/// <summary>
/// Guide listings read from live channel names.
/// </summary>
/// <remarks>
/// Every name here is a real one from a 12,253-channel catalog, which had about
/// 3,900 names carrying a time or a 24/7 marker. "Now" is a Friday evening in
/// New York, when those names were current.
/// </remarks>
public sealed class ChannelNameScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 18, 0, 0, TimeSpan.FromHours(-4));

    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static LiveChannel Channel(string name) => new()
    {
        Id = "ch",
        Name = name,
        CategoryId = "c",
        StreamUrl = "http://example/ch",
    };

    private static DateTimeOffset Utc(int month, int day, int hour, int minute = 0, int year = 2026) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    [Theory]
    // Machine stamps: Eastern, rounded down past the per-channel stagger.
    [InlineData("(FLSP 294) | hockey:  Tri_City Storm vs Lincoln Stars (Home) (2026-10-02 20:05:20)",
        "Hockey: Tri-City Storm vs Lincoln Stars (Home)", "2026-10-03T00:05Z")]
    [InlineData("US (ESPN+ 150) | Arkansas State vs. Georgia State Oct 02 7:00PM ET (2026-10-02 19:02:50)",
        "Arkansas State vs. Georgia State", "2026-10-02T23:00Z")]
    [InlineData("(TrillerTV 15) | VLC 2026_ Stage 1_ Day 2 (2026-10-07 01:00:12)",
        "VLC 2026 - Stage 1 - Day 2", "2026-10-07T05:00Z")]
    [InlineData("(Apple) (F1) 001 | Formula 1: Bahrain: Practice 2 [Main Broadcast]  (UHD) (2026-10-02 03:35:00)",
        "Formula 1: Bahrain: Practice 2 [Main Broadcast] (UHD)", "2026-10-02T07:35Z")]
    // AU-tagged stamps are Sydney: the 21:00 CEST Champions League kickoff.
    [InlineData("(AU) (K+ 097) | Man City v Real Madrid (2026-10-02 04:55:00)",
        "Man City v Real Madrid", "2026-10-01T18:55Z")]
    // 12-hour clocks: Eastern unless they say otherwise.
    [InlineData("NCAAF:08|San Diego State @ Toledo 12:00 PM ET CBSSN",
        "San Diego State @ Toledo CBSSN", "2026-10-02T16:00Z")]
    [InlineData("NFL  | 15 - SNF 8:20pm Lions at Panthers", "SNF Lions at Panthers", "2026-10-03T00:20Z")]
    [InlineData("NHL 4 Vancouver Canucks vs Edmonton Oilers 10PM",
        "NHL Vancouver Canucks vs Edmonton Oilers", "2026-10-03T02:00Z")]
    [InlineData("NCAAB 1 Michigan vs UConn 830PM", "NCAAB Michigan vs UConn", "2026-10-03T00:30Z")]
    [InlineData("LIVE EVENT 03 - 1pm Faour v Sachbazjan", "Faour v Sachbazjan", "2026-10-02T17:00Z")]
    [InlineData("(CA) Dazn  31: Red Owl Boxing: Cardenas vs Timko Oct 02 10:00 PM",
        "Red Owl Boxing: Cardenas vs Timko", "2026-10-03T02:00Z")]
    [InlineData("(CA) Dazn  33: WTA China Open: Beijing Open: Day 4 Oct 02 11:00 PM",
        "WTA China Open: Beijing Open: Day 4", "2026-10-03T03:00Z")]
    [InlineData("Grand Valley vs Davenport @ Oct 2 11:00 AM :Flo College  01",
        "Grand Valley vs Davenport", "2026-10-02T15:00Z")]
    [InlineData("UFC: UFC 332 Weigh In Desk Show @ Oct 2 10:50 AM :Paramount+  01",
        "UFC: UFC 332 Weigh In Desk Show", "2026-10-02T14:50Z")]
    [InlineData("RugbyPassTV 08ⓧ: Waikato v Tasman | Sat 3rd Oct 7:10AM UK",
        "Waikato v Tasman", "2026-10-03T06:10Z")]
    // 24-hour clocks: UK.
    [InlineData("Live Football 09 : Faroe Islands vs Slovakia 19:45 pm",
        "Faroe Islands vs Slovakia", "2026-10-02T18:45Z")]
    [InlineData("Scottish Cup 5: Spartans vs Ross County 15.00", "Spartans vs Ross County", "2026-10-02T14:00Z")]
    [InlineData("PSF 07 | PSF7: Spurs vs Hoffenheim 12:00", "Spurs vs Hoffenheim", "2026-10-02T11:00Z")]
    [InlineData("GaaGo 02: 14:00 GMT Dublin: Lucan Sarsfields v Erins Isle",
        "Dublin: Lucan Sarsfields v Erins Isle", "2026-10-02T13:00Z")]
    [InlineData("Gaago 01:  Fri 19:00 | Antrim: Naomh Seamus vs St Mary's Ahoghill",
        "Antrim: Naomh Seamus vs St Mary's Ahoghill", "2026-10-02T18:00Z")]
    [InlineData("Flo (FLSP) 149: 2025 Caltech vs Redlands - Mens - 22/10 22:00",
        "2025 Caltech vs Redlands - Mens", "2026-10-22T21:00Z")]
    // Two zones: the first is used.
    [InlineData("NETFLIX WWE WrestleMania 42 Sunday | 2026.04.19 | 23:00 UK / 18:00 USA",
        "NETFLIX WWE WrestleMania 42 Sunday", "2026-04-19T22:00Z")]
    [InlineData("LOI TV 3 | Galway United v Bohemians // UK Fri 11 Sep 7:30pm // ET Fri 11 Sep 2:30pm",
        "Galway United v Bohemians", "2026-09-11T18:30Z")]
    public void ReadsTheEventAndItsStart(string name, string title, string startUtc)
    {
        var listing = ChannelNameSchedule.Parse(name, Now);

        Assert.NotNull(listing);
        Assert.False(listing.IsAlwaysOn);
        Assert.Equal(title, listing.Title);
        Assert.Equal(DateTimeOffset.Parse(startUtc), listing.Start);
    }

    [Theory]
    [InlineData("US (ESPN+ 150) | Arkansas State vs. Georgia State (2026-10-02 19:02:50)", true)]
    [InlineData("(CA) Dazn  31: Red Owl Boxing: Cardenas vs Timko Oct 02 10:00 PM", true)]
    [InlineData("Gaago 01:  Fri 19:00 | Antrim: Naomh Seamus vs St Mary's Ahoghill", true)]
    [InlineData("NHL | 02 - 7pm Capitals at Hurricanes", false)]
    [InlineData("Live Football 09 : Faroe Islands vs Slovakia 19:45 pm", false)]
    public void SaysWhetherTheNameGaveADate(string name, bool hasDate) =>
        Assert.Equal(hasDate, ChannelNameSchedule.Parse(name, Now)?.HasDate);

    [Fact]
    public void ReadsAStatedStopTime()
    {
        var listing = ChannelNameSchedule.Parse(
            "MLB 01 :  Phillies x Braves start:2026-10-01 19:00:00 stop:2026-10-02 02:13:20", Now);

        Assert.NotNull(listing);
        Assert.Equal("Phillies x Braves", listing.Title);
        Assert.Equal(Utc(10, 1, 23), listing.Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 6, 13, 20, TimeSpan.Zero), listing.Stop);
    }

    [Theory]
    [InlineData("US (ESPNPlay 237) |  (2098-12-31 08:00:10)")]
    [InlineData("UK (MAX 073) |  (2098-12-31 13:00:30)")]
    public void SkipsSlotsTheProviderHasNotFilled(string name) =>
        Assert.Null(ChannelNameSchedule.Parse(name, Now));

    [Theory]
    [InlineData("ENGLISH SERIES | ASH VS EVIL DEAD HD")]
    [InlineData("USA Locals: US abc2 (KMID) Odessa/Midland (V)")]
    [InlineData("CA HD: CANAL V")]
    [InlineData("Event Recording 02: Canelo vs Crawford")]
    [InlineData("NBA Summer League Raptors vs. Heat jul 17 :NBA 05")]
    [InlineData("")]
    [InlineData(null)]
    public void LeavesNamesWithNoTimeAlone(string? name) =>
        Assert.Null(ChannelNameSchedule.Parse(name, Now));

    [Fact]
    public void RejectsAWeekdayThatDoesNotFitTheDate()
    {
        // The nearest 6th is Tuesday 6 October: this name is from 2023.
        Assert.Null(ChannelNameSchedule.Parse(
            "Dirtvision 01: Opening Night of the 52nd Federated Auto Parts DIRTcar Nationals! |  Mon 6th 5:15PM", Now));
    }

    [Theory]
    [InlineData("24/7 Young Sheldon", "Young Sheldon")]
    [InlineData("24/7 Law & Order: SVU", "Law & Order: SVU")]
    [InlineData("AFL | 24/7", "AFL")]
    [InlineData("NRL TV 24/7 (502)", "NRL TV")]
    [InlineData("News USA: Dateline 24/7", "Dateline")]
    [InlineData("Dirtvision 12: 24/7 DIRTVISION", "DIRTVISION")]
    [InlineData("Flo (FLSP) 6: FloRacing 24/7 FloRacing 247", "FloRacing")]
    [InlineData("US (UFC 01) | Fight Pass 24/7 (2019-09-25 19:00:00)", "Fight Pass")]
    public void ReadsTheShowOnAnAlwaysOnChannel(string name, string title)
    {
        var listing = ChannelNameSchedule.Parse(name, Now);

        Assert.NotNull(listing);
        Assert.True(listing.IsAlwaysOn);
        Assert.Equal(title, listing.Title);
        Assert.Null(listing.Start);
    }

    [Fact]
    public void AnAlwaysOnChannelCoversTheWindowHourByHour()
    {
        var from = Now.AddMinutes(-150);
        var to = Now.AddHours(28);

        var programmes = ChannelNameSchedule.Programmes(Channel("24/7 Young Sheldon"), from, to, Now, NewYork);

        // 15:30 Friday to 22:00 Saturday, from the hour before the window starts.
        Assert.Equal(31, programmes.Count);
        Assert.All(programmes, p => Assert.True(p.IsFromChannelName));
        Assert.All(programmes, p => Assert.Equal("Young Sheldon", p.Title));
        Assert.All(programmes, p => Assert.Equal(TimeSpan.FromHours(1), p.Duration));
        Assert.True(programmes[0].Start <= from);
        Assert.True(programmes[^1].Stop >= to);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 15, 0, 0, TimeSpan.FromHours(-4)), programmes[0].Start);

        for (var i = 1; i < programmes.Count; i++)
        {
            Assert.Equal(programmes[i - 1].Stop, programmes[i].Start);
        }
    }

    [Fact]
    public void AnEventRunsFourHoursByDefault()
    {
        var programmes = ChannelNameSchedule.Programmes(
            Channel("NHL | 02 - 7pm Capitals at Hurricanes"), Now.AddHours(-2), Now.AddHours(28), Now);

        var programme = Assert.Single(programmes);
        Assert.Equal(Utc(10, 2, 23), programme.Start);
        Assert.Equal(ChannelNameSchedule.DefaultEventLength, programme.Duration);
        Assert.True(programme.IsFromChannelName);
        Assert.Contains("estimate", programme.Description);
    }

    [Fact]
    public void AStatedStopIsUsed()
    {
        var programmes = ChannelNameSchedule.Programmes(
            Channel("LOI TV 1 | Wexford v Galway United  start: 2026-10-02 17:45:00  stop: 2026-10-02 20:45:00"),
            Now.AddHours(-2), Now.AddHours(28), Now);

        Assert.Equal(TimeSpan.FromHours(3), Assert.Single(programmes).Duration);
    }

    [Fact]
    public void AStatedStopMoreThanTwelveHoursOutIsIgnored()
    {
        var programmes = ChannelNameSchedule.Programmes(
            Channel("MLB 03 : Mets x Cubs start:2026-10-02 19:00:00 stop:2026-10-03 19:00:00"),
            Now.AddHours(-2), Now.AddHours(28), Now);

        Assert.Equal(ChannelNameSchedule.DefaultEventLength, Assert.Single(programmes).Duration);
    }

    [Fact]
    public void ALateGameIsStillOnAfterMidnight()
    {
        var oneAm = new DateTimeOffset(2026, 10, 3, 1, 0, 0, TimeSpan.FromHours(-4));

        var programmes = ChannelNameSchedule.Programmes(
            Channel("NHL 4 Vancouver Canucks vs Edmonton Oilers 10PM"), oneAm.AddHours(-2), oneAm.AddHours(28), oneAm);

        var programme = Assert.Single(programmes);
        Assert.Equal(Utc(10, 3, 2), programme.Start);
        Assert.True(programme.IsNowPlaying(oneAm));
    }

    [Fact]
    public void AMonthAndDayTakeTheNearestYear()
    {
        var newYearsEve = new DateTimeOffset(2026, 12, 30, 12, 0, 0, TimeSpan.FromHours(-5));

        var listing = ChannelNameSchedule.Parse("(CA) Dazn  01: Boxing: A vs B Jan 2 7:00 PM", newYearsEve);

        Assert.NotNull(listing);
        Assert.Equal(Utc(1, 3, 0, year: 2027), listing.Start);
    }

    [Theory]
    [InlineData("US (Peacock 100) | Celtics vs. Wizards (2026-11-24 19:00:00)")]
    [InlineData("US (ESPN+ 001) | Long Over (2026-10-01 08:00:00)")]
    public void AnEventOutsideTheWindowHasNoListing(string name) =>
        Assert.Empty(ChannelNameSchedule.Programmes(Channel(name), Now.AddHours(-2), Now.AddHours(28), Now));

    [Fact]
    public void ANameWithNoListingHasNoProgrammes() =>
        Assert.Empty(ChannelNameSchedule.Programmes(
            Channel("CA HD: CANAL V"), Now.AddHours(-2), Now.AddHours(28), Now));
}
