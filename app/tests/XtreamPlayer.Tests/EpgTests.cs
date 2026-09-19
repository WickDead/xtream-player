using System.Text;
using XtreamPlayer.Core.Library;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Infrastructure.Epg;
using Xunit;

namespace XtreamPlayer.Tests;

public sealed class XmltvParserTests
{
    private static async Task<(List<EpgChannel> Channels, List<EpgProgramme> Programmes)> ParseAsync(
        string xml, XmltvParser? parser = null)
    {
        var channels = new List<EpgChannel>();
        var programmes = new List<EpgProgramme>();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        await (parser ?? new XmltvParser()).ParseAsync(
            stream,
            (channel, _) => { channels.Add(channel); return Task.CompletedTask; },
            (programme, _) => { programmes.Add(programme); return Task.CompletedTask; });

        return (channels, programmes);
    }

    /// <summary>Built relative to now, so the window filter does not discard it.</summary>
    private static string Document(string body) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <tv>
          <channel id="bbc1.uk">
            <display-name>BBC One</display-name>
            <icon src="http://example.invalid/bbc1.png" />
          </channel>
          {body}
        </tv>
        """;

    private static string Stamp(TimeSpan fromNow) =>
        DateTimeOffset.UtcNow.Add(fromNow).ToString("yyyyMMddHHmmss") + " +0000";

    [Fact]
    public async Task ReadsChannelsAndProgrammes()
    {
        var (channels, programmes) = await ParseAsync(Document($"""
            <programme start="{Stamp(TimeSpan.FromMinutes(-10))}"
                       stop="{Stamp(TimeSpan.FromMinutes(50))}" channel="bbc1.uk">
              <title>BBC News</title>
              <sub-title>Late edition</sub-title>
              <desc>The latest headlines.</desc>
              <category>News</category>
            </programme>
            """));

        var channel = Assert.Single(channels);
        Assert.Equal("bbc1.uk", channel.XmltvId);
        Assert.Equal("BBC One", channel.DisplayName);
        Assert.Equal("http://example.invalid/bbc1.png", channel.IconUrl);

        var programme = Assert.Single(programmes);
        Assert.Equal("BBC News", programme.Title);
        Assert.Equal("Late edition", programme.SubTitle);
        Assert.Equal("News", programme.Category);
        Assert.Equal(TimeSpan.FromMinutes(60), programme.Duration);
    }

    /// <summary>
    /// A missing stop is common in real feeds. Discarding the programme would
    /// leave visible holes in the grid.
    /// </summary>
    [Fact]
    public async Task AProgrammeWithNoStopGetsADefaultLength()
    {
        var (_, programmes) = await ParseAsync(Document($"""
            <programme start="{Stamp(TimeSpan.FromMinutes(5))}" channel="bbc1.uk">
              <title>Continuity</title>
            </programme>
            """));

        Assert.Equal(TimeSpan.FromMinutes(30), Assert.Single(programmes).Duration);
    }

    [Fact]
    public async Task ProgrammesWithNoTitleGetAPlaceholder()
    {
        var (_, programmes) = await ParseAsync(Document($"""
            <programme start="{Stamp(TimeSpan.FromMinutes(1))}"
                       stop="{Stamp(TimeSpan.FromMinutes(31))}" channel="bbc1.uk" />
            """));

        Assert.Equal("Unknown programme", Assert.Single(programmes).Title);
    }

    /// <summary>One malformed entry must not cost the rest of the feed.</summary>
    [Fact]
    public async Task SkipsProgrammesWithNoChannelOrStart()
    {
        var (_, programmes) = await ParseAsync(Document($"""
            <programme stop="{Stamp(TimeSpan.FromMinutes(30))}" channel="bbc1.uk">
              <title>No start</title>
            </programme>
            <programme start="{Stamp(TimeSpan.FromMinutes(1))}"
                       stop="{Stamp(TimeSpan.FromMinutes(31))}">
              <title>No channel</title>
            </programme>
            <programme start="{Stamp(TimeSpan.FromMinutes(1))}"
                       stop="{Stamp(TimeSpan.FromMinutes(31))}" channel="bbc1.uk">
              <title>Good one</title>
            </programme>
            """));

        Assert.Equal("Good one", Assert.Single(programmes).Title);
    }

    /// <summary>
    /// Feeds routinely carry a fortnight of data. Storing all of it makes every
    /// query slower for listings nobody will scroll to.
    /// </summary>
    [Fact]
    public async Task ProgrammesOutsideTheWindowAreDropped()
    {
        var parser = new XmltvParser
        {
            PastWindow = TimeSpan.FromHours(1),
            FutureWindow = TimeSpan.FromHours(2),
        };

        var (_, programmes) = await ParseAsync(Document($"""
            <programme start="{Stamp(TimeSpan.FromDays(-3))}"
                       stop="{Stamp(TimeSpan.FromDays(-3) + TimeSpan.FromMinutes(30))}" channel="bbc1.uk">
              <title>Long finished</title>
            </programme>
            <programme start="{Stamp(TimeSpan.FromDays(9))}"
                       stop="{Stamp(TimeSpan.FromDays(9) + TimeSpan.FromMinutes(30))}" channel="bbc1.uk">
              <title>Far future</title>
            </programme>
            <programme start="{Stamp(TimeSpan.FromMinutes(10))}"
                       stop="{Stamp(TimeSpan.FromMinutes(40))}" channel="bbc1.uk">
              <title>In window</title>
            </programme>
            """), parser);

        Assert.Equal("In window", Assert.Single(programmes).Title);
    }

    [Theory]
    // The shape the spec describes.
    [InlineData("20260918120000 +0000", 12, 0)]
    [InlineData("20260918120000 +0100", 11, 0)]
    // Real feeds also omit the space, the seconds, or the offset entirely.
    [InlineData("20260918120000+0000", 12, 0)]
    [InlineData("202609181200 +0000", 12, 0)]
    [InlineData("20260918120000", 12, 0)]
    public void ParsesTheTimestampShapesFeedsActuallyUse(string raw, int hour, int minute)
    {
        var parsed = XmltvParser.ParseTimestamp(raw);

        Assert.NotNull(parsed);
        Assert.Equal(hour, parsed!.Value.UtcDateTime.Hour);
        Assert.Equal(minute, parsed.Value.UtcDateTime.Minute);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a date")]
    public void RejectsUnparseableTimestamps(string? raw) =>
        Assert.Null(XmltvParser.ParseTimestamp(raw));

    /// <summary>
    /// The XMLTV spec puts a DOCTYPE on every document and real providers emit
    /// one, so rejecting it outright would fail on essentially every feed.
    /// </summary>
    [Fact]
    public async Task AcceptsTheDoctypeRealFeedsCarry()
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8" ?><!DOCTYPE tv SYSTEM "xmltv.dtd">
            <tv generator-info-name="NXT">
              <channel id="BBC1.uk">
                <display-name>Basic UK: BBC One</display-name>
                <icon src="https://example.invalid/bbcone.png" />
              </channel>
              <programme start="{Stamp(TimeSpan.FromMinutes(-5))}"
                         stop="{Stamp(TimeSpan.FromMinutes(25))}" channel="BBC1.uk">
                <title>BBC News</title>
              </programme>
            </tv>
            """;

        var (channels, programmes) = await ParseAsync(xml);

        Assert.Single(channels);
        Assert.Equal("https://example.invalid/bbcone.png", channels[0].IconUrl);
        Assert.Single(programmes);
    }

    /// <summary>
    /// Tolerating the DOCTYPE must not mean processing it: an external entity is
    /// still never resolved.
    /// </summary>
    [Fact]
    public async Task NeverExpandsEntities()
    {
        const string malicious = """
            <?xml version="1.0"?>
            <!DOCTYPE tv [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <tv><channel id="x"><display-name>&xxe;</display-name></channel></tv>
            """;

        // Ignoring the DTD leaves the entity undefined, so referencing it is an
        // error rather than a file read.
        await Assert.ThrowsAnyAsync<System.Xml.XmlException>(() => ParseAsync(malicious));
    }
}

public sealed class EpgChannelMatcherTests
{
    private static readonly EpgChannelMatcher.Candidate[] Candidates =
    [
        new("bbc1.uk", "BBC One"),
        new("tsn1.ca", "TSN 1"),
        new("sportsnet.ca", "Sportsnet Ontario"),
    ];

    private static EpgChannelMapping MatchOne(string name, string? epgId = null) =>
        EpgChannelMatcher.Match(
            [new EpgChannelMatcher.Input("1", name, epgId)],
            Candidates)[0];

    [Fact]
    public void ExactIdWins()
    {
        var mapping = MatchOne("Anything at all", "bbc1.uk");

        Assert.Equal("bbc1.uk", mapping.XmltvChannelId);
        Assert.Equal(EpgMatchMethod.ExactId, mapping.Method);
    }

    [Theory]
    [InlineData("BBC1.UK")]
    [InlineData(" bbc1.uk ")]
    [InlineData("bbc1uk")]
    public void IdsMatchIgnoringCaseAndPunctuation(string epgId)
    {
        var mapping = MatchOne("BBC One", epgId);

        Assert.Equal("bbc1.uk", mapping.XmltvChannelId);
    }

    /// <summary>
    /// Provider names carry a category prefix and a quality suffix, neither of
    /// which is part of the channel's identity. Without stripping them almost
    /// nothing matches.
    /// </summary>
    [Theory]
    [InlineData("Sports CA: TSN 1")]
    [InlineData("Sports CA: TSN 1 HD")]
    [InlineData("UK | TSN 1 FHD")]
    [InlineData("TSN 1")]
    [InlineData("TSN1")]
    public void MatchesByNameThroughProviderDecoration(string name)
    {
        var mapping = MatchOne(name);

        Assert.Equal("tsn1.ca", mapping.XmltvChannelId);
        Assert.Equal(EpgMatchMethod.DisplayName, mapping.Method);
    }

    /// <summary>
    /// "TSN 10" is not "TSN 1". Stripping decoration must not go so far that
    /// distinct channels collapse together.
    /// </summary>
    [Theory]
    [InlineData("Sports CA: TSN 10")]
    [InlineData("Discovery Channel")]
    [InlineData("Random Channel 5")]
    public void DoesNotMatchDifferentChannels(string name) =>
        Assert.False(MatchOne(name).IsMatched);

    [Fact]
    public void UnmatchedChannelsAreRecordedRatherThanDropped()
    {
        var mapping = MatchOne("Nothing like this exists");

        Assert.False(mapping.IsMatched);
        Assert.Equal(EpgMatchMethod.None, mapping.Method);
        Assert.Equal("1", mapping.ChannelId);
    }

    /// <summary>A choice the user made must survive every later refresh.</summary>
    [Fact]
    public void UserOverridesAreNeverReGuessed()
    {
        var existing = new Dictionary<string, EpgChannelMapping>
        {
            ["1"] = new()
            {
                ChannelId = "1",
                XmltvChannelId = "sportsnet.ca",
                Method = EpgMatchMethod.UserOverride,
                IsUserOverride = true,
            },
        };

        var mapping = EpgChannelMatcher.Match(
            [new EpgChannelMatcher.Input("1", "BBC One", "bbc1.uk")],
            Candidates,
            existing)[0];

        // The exact id would otherwise have won.
        Assert.Equal("sportsnet.ca", mapping.XmltvChannelId);
        Assert.True(mapping.IsUserOverride);
    }

    /// <summary>
    /// Providers frequently put a channel NAME in epg_channel_id.
    /// </summary>
    [Fact]
    public void AnIdThatIsReallyANameStillMatches()
    {
        var mapping = MatchOne("Sports CA: TSN 1", "TSN 1");

        Assert.Equal("tsn1.ca", mapping.XmltvChannelId);
    }

    [Theory]
    [InlineData("BBC One HD", "bbcone")]
    [InlineData("Sports CA: TSN 1", "tsn1")]
    [InlineData("UK | BBC One FHD", "bbcone")]
    // Decoration is only stripped from the end: "HD Movies" is a real name.
    [InlineData("HD Movies", "hdmovies")]
    // Never strip away the whole name.
    [InlineData("HD", "hd")]
    public void NormalizesNamesPredictably(string input, string expected) =>
        Assert.Equal(expected, EpgChannelMatcher.NormalizeName(input));
}

/// <summary>
/// Parses a real provider feed. Opt-in: set XP_XMLTV to a downloaded
/// xmltv.php response.
/// </summary>
/// <remarks>
/// Synthetic fixtures cannot tell you that a real feed opens with a DOCTYPE the
/// parser was configured to reject, or that it reuses one XMLTV id across
/// several channels. Only a real feed does.
/// </remarks>
public sealed class XmltvRealFeedTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static string? FeedPath
    {
        get
        {
            var path = Environment.GetEnvironmentVariable("XP_XMLTV");
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
        }
    }

    [SkippableFact]
    public async Task ParsesTheProvidersOwnFeed()
    {
        Skip.If(FeedPath is null, "Set XP_XMLTV to run this.");

        var channels = new List<EpgChannel>();
        var programmes = new List<EpgProgramme>();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var before = GC.GetTotalAllocatedBytes();

        await using var stream = File.OpenRead(FeedPath!);

        var result = await new XmltvParser().ParseAsync(
            stream,
            (channel, _) => { channels.Add(channel); return Task.CompletedTask; },
            (programme, _) => { programmes.Add(programme); return Task.CompletedTask; });

        stopwatch.Stop();
        var allocated = (GC.GetTotalAllocatedBytes() - before) / 1024d / 1024d;

        output.WriteLine(
            $"{result.ChannelCount:N0} channels, {result.ProgrammeCount:N0} programmes "
          + $"in {stopwatch.ElapsedMilliseconds} ms, ~{allocated:F0} MB allocated");

        Assert.NotEmpty(channels);
        Assert.NotEmpty(programmes);

        // Icons are read from a sibling element of display-name, which is
        // exactly what the reader-advancement bug silently dropped.
        var withIcons = channels.Count(c => c.IconUrl is not null);
        var withNames = channels.Count(c => c.DisplayName is not null);

        output.WriteLine($"channels with a display name: {withNames:N0}, with an icon: {withIcons:N0}");

        Assert.True(withNames > 0, "No channel had a display name.");

        foreach (var programme in programmes.Take(2000))
        {
            Assert.False(string.IsNullOrWhiteSpace(programme.Title));
            Assert.True(programme.Stop > programme.Start,
                $"'{programme.Title}' ends before it starts.");
        }

        var sample = programmes[0];
        output.WriteLine(
            $"first: {sample.Start.ToLocalTime():yyyy-MM-dd HH:mm} "
          + $"{sample.Title} ({sample.XmltvChannelId})");
    }
}
