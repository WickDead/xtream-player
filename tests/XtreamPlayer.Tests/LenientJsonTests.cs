using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Infrastructure.Xtream;
using XtreamPlayer.Infrastructure.Xtream.Json;
using Xunit;

namespace XtreamPlayer.Tests;

/// <summary>
/// The provider-tolerance layer. These are the highest-value tests in the
/// project: every case here is a shape a real Xtream server sends, and any one
/// of them breaks an entire catalog if mishandled.
/// </summary>
public sealed class LenientJsonTests
{
    private sealed class Probe
    {
        public string? name { get; set; }
        public long? count { get; set; }
        public double? score { get; set; }
        public string? after { get; set; }
    }

    private static Probe Parse(string json) =>
        JsonSerializer.Deserialize<Probe>(json, XtreamJson.Options)!;

    [Theory]
    [InlineData("""{"name":"Sportsnet"}""", "Sportsnet")]
    [InlineData("""{"name":null}""", null)]
    [InlineData("""{"name":""}""", null)]
    [InlineData("""{"name":"   "}""", null)]
    // Providers send numeric ids and counts where a string is documented.
    [InlineData("""{"name":1234}""", "1234")]
    [InlineData("""{"name":12.5}""", "12.5")]
    // And booleans for flag-ish fields.
    [InlineData("""{"name":true}""", "1")]
    [InlineData("""{"name":false}""", "0")]
    public void StringsAbsorbEveryShape(string json, string? expected) =>
        Assert.Equal(expected, Parse(json).name);

    [Theory]
    [InlineData("""{"count":42}""", 42L)]
    [InlineData("""{"count":"42"}""", 42L)]
    // "12.0" appears in the wild for ids.
    [InlineData("""{"count":"12.0"}""", 12L)]
    [InlineData("""{"count":12.9}""", 12L)]
    [InlineData("""{"count":""}""", null)]
    [InlineData("""{"count":null}""", null)]
    [InlineData("""{"count":"not a number"}""", null)]
    [InlineData("""{"count":true}""", 1L)]
    public void IntegersAbsorbEveryShape(string json, long? expected) =>
        Assert.Equal(expected, Parse(json).count);

    /// <summary>
    /// The single most likely porting bug. Utf8JsonReader is a forward-only
    /// cursor: returning from a converter without consuming an array or object
    /// desynchronizes the rest of the document, and the symptom is a parse
    /// failure far away from the cause.
    /// </summary>
    [Theory]
    [InlineData("""{"name":["a","b"],"after":"survived"}""")]
    [InlineData("""{"name":{"nested":{"deep":[1,2,3]}},"after":"survived"}""")]
    [InlineData("""{"count":[1,2,3],"after":"survived"}""")]
    [InlineData("""{"score":{"x":1},"after":"survived"}""")]
    public void UnexpectedContainersDoNotDesyncTheReader(string json)
    {
        var probe = Parse(json);

        Assert.Null(probe.name ?? null);
        Assert.Equal("survived", probe.after);
    }

    /// <summary>
    /// Rust's number formatting is culture-free; .NET's is not. On a German
    /// machine double.ToString() yields "7,5", which then fails to parse back
    /// and silently drops every rating in the catalog.
    /// </summary>
    [Fact]
    public void NumberHandlingIsCultureIndependent()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.Equal("12.5", Parse("""{"name":12.5}""").name);
            Assert.Equal(12L, Parse("""{"count":"12.5"}""").count);
            Assert.Equal("7.5", XtreamText.NormalizeRating("7.50"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    private sealed class ListProbe
    {
        [JsonConverter(typeof(LenientStringListConverter))]
        public List<string>? formats { get; set; }

        public string? after { get; set; }
    }

    [Fact]
    public void StringListsAcceptArraysSingleValuesAndJunk()
    {
        var options = XtreamJson.Options;

        var array = JsonSerializer.Deserialize<ListProbe>(
            """{"formats":["m3u8","ts","rtmp"],"after":"ok"}""", options)!;
        Assert.Equal(["m3u8", "ts", "rtmp"], array.formats);
        Assert.Equal("ok", array.after);

        var single = JsonSerializer.Deserialize<ListProbe>(
            """{"formats":"ts","after":"ok"}""", options)!;
        Assert.Equal(["ts"], single.formats);

        // Mixed junk inside the array must not abort the whole parse.
        var mixed = JsonSerializer.Deserialize<ListProbe>(
            """{"formats":["ts",5,null,{"x":1},"m3u8"],"after":"ok"}""", options)!;
        Assert.Equal(["ts", "5", "m3u8"], mixed.formats);
        Assert.Equal("ok", mixed.after);
    }

    /// <summary>
    /// A blocked or expired account gets an error object where the catalog array
    /// belongs. The Rust client raised InvalidPayload and lost the provider's
    /// message; an empty catalog is the recoverable outcome.
    /// </summary>
    [Fact]
    public void CatalogListsSurviveAnObjectWhereAnArrayBelongs()
    {
        var response = JsonSerializer.Deserialize<RawShortEpgResponse>(
            """{"epg_listings":{"error":"account blocked"}}""", XtreamJson.Options)!;

        Assert.Empty(response.EpgListings);
    }

    /// <summary>
    /// episodes is an object keyed by season for most providers, but an empty
    /// array for a series with none. Dictionary&lt;string,...&gt; throws on [].
    /// </summary>
    [Theory]
    [InlineData("""{"episodes":[]}""", 0)]
    [InlineData("""{"episodes":null}""", 0)]
    [InlineData("""{"episodes":{"1":[{"id":"1","title":"Pilot"}]}}""", 1)]
    public void EpisodeMapsSurviveBothShapes(string json, int expectedSeasons)
    {
        var response = JsonSerializer.Deserialize<RawSeriesInfoResponse>(json, XtreamJson.Options)!;

        Assert.Equal(expectedSeasons, response.episodes.Count);
    }

    [Theory]
    [InlineData("""{"added":1700000000}""")]
    [InlineData("""{"added":"1700000000"}""")]
    public void UnixSecondsParseFromNumberOrString(string json)
    {
        var item = JsonSerializer.Deserialize<RawVodStream>(json, XtreamJson.Options)!;

        Assert.NotNull(item.added);
        Assert.Equal(2023, item.added!.Value.Year);
    }

    [Theory]
    // Providers use 0 to mean "unknown" far more often than they mean 1970.
    [InlineData("""{"added":0}""")]
    [InlineData("""{"added":""}""")]
    [InlineData("""{"added":null}""")]
    [InlineData("""{"added":"garbage"}""")]
    public void ZeroAndJunkTimestampsBecomeNull(string json) =>
        Assert.Null(JsonSerializer.Deserialize<RawVodStream>(json, XtreamJson.Options)!.added);

    [Fact]
    public void UnknownFieldsAreIgnored()
    {
        var item = JsonSerializer.Deserialize<RawLiveStream>(
            """{"stream_id":"604","name":"Sportsnet","brand_new_field":{"a":[1,2]},"num":"12"}""",
            XtreamJson.Options)!;

        Assert.Equal("604", item.StreamId);
        Assert.Equal("Sportsnet", item.name);
        Assert.Equal("12", item.num);
    }
}

public sealed class RatingAndEpgTextTests
{
    [Theory]
    [InlineData("7", "7")]
    [InlineData("7.50", "7.5")]
    // Non-integral values keep one decimal place, matching the Rust original.
    [InlineData("7.04", "7.0")]
    [InlineData("8.96", "9.0")]
    // Every provider convention for "no rating".
    [InlineData("0", null)]
    [InlineData("0.0", null)]
    [InlineData("-1", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData("N/A", null)]
    public void NormalizeRatingMatchesTheRustBehaviour(string? input, string? expected) =>
        Assert.Equal(expected, XtreamText.NormalizeRating(input));

    [Fact]
    public void DecodesBase64EpgTitles()
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Hockey Night in Canada"));

        Assert.Equal("Hockey Night in Canada", XtreamText.DecodeEpgText(encoded));
    }

    /// <summary>
    /// The guard the Rust version lacked. A short ASCII title can itself be
    /// valid base64, and a naive try-decode turns real titles into mojibake.
    /// </summary>
    [Theory]
    [InlineData("News")]
    [InlineData("Film")]
    [InlineData("NCIS")]
    [InlineData("Sportsnet Central")]
    public void PlainTitlesAreNotMistakenForBase64(string title) =>
        Assert.Equal(title, XtreamText.DecodeEpgText(title));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingTitlesGetAPlaceholder(string? input) =>
        Assert.Equal("Unknown programme", XtreamText.DecodeEpgText(input));

    [Fact]
    public void DecodingNonTextBytesFallsBackToTheRawValue()
    {
        // Valid base64, but the bytes are not text.
        var binary = Convert.ToBase64String([0x00, 0x01, 0x02, 0x03, 0xFF, 0xFE]);

        Assert.Equal(binary, XtreamText.DecodeEpgText(binary));
    }
}

public sealed class XtreamUrlBuilderTests
{
    private static XtreamUrlBuilder Builder(string url = "example.pw", string pass = "pw") =>
        new(url, "user", pass);

    [Theory]
    [InlineData("example.pw")]
    [InlineData("http://example.pw")]
    [InlineData("http://example.pw/")]
    [InlineData("http://example.pw///")]
    public void NormalizesTheBaseUrl(string input) =>
        Assert.Equal("http://example.pw/live/user/pw/604.ts", Builder(input).Live("604", "ts"));

    /// <summary>
    /// The old Rust client interpolated credentials into the path raw, so a
    /// password containing a URL-significant character produced a silently
    /// malformed URL and every stream failed.
    /// </summary>
    [Theory]
    [InlineData("pa/ss", "pa%2Fss")]
    [InlineData("pa?ss", "pa%3Fss")]
    [InlineData("pa#ss", "pa%23ss")]
    [InlineData("pa@ss", "pa%40ss")]
    [InlineData("pa ss", "pa%20ss")]
    public void EscapesCredentialsInThePath(string password, string expected) =>
        Assert.Contains($"/user/{expected}/", Builder(pass: password).Live("604", "ts"),
            StringComparison.Ordinal);

    [Theory]
    // The provider offers both; ts wins because libvlc decodes it natively and
    // it has lower latency.
    [InlineData(new[] { "m3u8", "ts", "rtmp" }, LiveStreamFormat.Auto, "ts")]
    [InlineData(new[] { "m3u8" }, LiveStreamFormat.Auto, "m3u8")]
    [InlineData(new string[0], LiveStreamFormat.Auto, "ts")]
    // An explicit per-profile override always wins.
    [InlineData(new[] { "m3u8" }, LiveStreamFormat.Ts, "ts")]
    [InlineData(new[] { "ts" }, LiveStreamFormat.Hls, "m3u8")]
    public void ChoosesTheLiveContainer(string[] allowed, LiveStreamFormat preference, string expected) =>
        Assert.Equal(expected, XtreamUrlBuilder.ChooseLiveExtension(allowed, preference));

    [Theory]
    [InlineData("mkv", "mkv")]
    [InlineData(".mkv", "mkv")]
    [InlineData("MKV", "mkv")]
    [InlineData(null, "mp4")]
    [InlineData("", "mp4")]
    // Junk would corrupt the URL.
    [InlineData("../../etc", "mp4")]
    [InlineData("a-very-long-extension", "mp4")]
    public void CleansTheContainerExtension(string? container, string expected) =>
        Assert.EndsWith($".{expected}", Builder().Movie("1", container), StringComparison.Ordinal);

    /// <summary>
    /// Some providers serve episodes from a different host than the catalog, so
    /// a constructed URL 404s. direct_source wins when it is a real URL.
    /// </summary>
    [Fact]
    public void PrefersDirectSourceForEpisodes() =>
        Assert.Equal("http://cdn.example.pw/ep/9.mkv",
            Builder().Episode("9", "mkv", "http://cdn.example.pw/ep/9.mkv"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.pw/x")]
    public void FallsBackWhenDirectSourceIsUnusable(string? directSource) =>
        Assert.Equal("http://example.pw/series/user/pw/9.mkv",
            Builder().Episode("9", "mkv", directSource));
}

public sealed class SeasonInferenceTests
{
    private static SeriesEpisode Episode(int season, int number) => new()
    {
        Id = $"{season}-{number}",
        Title = $"E{number}",
        SeasonNumber = season,
        EpisodeNumber = number,
        StreamUrl = "http://example.pw/x.mp4",
    };

    /// <summary>
    /// Real providers routinely return an empty seasons array with a fully
    /// populated episodes map. Read naively, the series appears to have no
    /// content at all.
    /// </summary>
    [Fact]
    public void InfersSeasonsFromTheEpisodeMapWhenNoneAreDeclared()
    {
        var episodes = new Dictionary<int, IReadOnlyList<SeriesEpisode>>
        {
            [2] = [Episode(2, 1), Episode(2, 2)],
            [1] = [Episode(1, 1)],
        };

        var seasons = XtreamClient.BuildSeasons([], episodes);

        Assert.Equal(2, seasons.Count);
        Assert.Equal([1, 2], seasons.Select(s => s.Number));
        Assert.Equal(1, seasons[0].EpisodeCount);
        Assert.Equal(2, seasons[1].EpisodeCount);
    }

    [Fact]
    public void DeclaredSeasonsWithNoEpisodesStillAppear()
    {
        var declared = new List<RawSeason>
        {
            new() { SeasonNumber = 1, name = "Season One", EpisodeCount = 10 },
        };

        var seasons = XtreamClient.BuildSeasons(declared, []);

        var season = Assert.Single(seasons);
        Assert.Equal("Season One", season.Name);
        // Falls back to the declared count when the episode map is empty.
        Assert.Equal(10, season.EpisodeCount);
    }

    [Fact]
    public void TheEpisodeMapWinsOverADeclaredCount()
    {
        var declared = new List<RawSeason> { new() { SeasonNumber = 1, EpisodeCount = 99 } };
        var episodes = new Dictionary<int, IReadOnlyList<SeriesEpisode>>
        {
            [1] = [Episode(1, 1), Episode(1, 2)],
        };

        Assert.Equal(2, Assert.Single(XtreamClient.BuildSeasons(declared, episodes)).EpisodeCount);
    }

    [Fact]
    public void SeasonZeroIsNamedSpecials()
    {
        var episodes = new Dictionary<int, IReadOnlyList<SeriesEpisode>> { [0] = [Episode(0, 1)] };

        Assert.Equal("Specials", Assert.Single(XtreamClient.BuildSeasons([], episodes)).Name);
    }

    [Fact]
    public void MergesDeclaredAndInferredSeasons()
    {
        var declared = new List<RawSeason> { new() { SeasonNumber = 1, name = "One" } };
        var episodes = new Dictionary<int, IReadOnlyList<SeriesEpisode>>
        {
            [1] = [Episode(1, 1)],
            [2] = [Episode(2, 1)],
        };

        var seasons = XtreamClient.BuildSeasons(declared, episodes);

        Assert.Equal(2, seasons.Count);
        Assert.Equal("One", seasons[0].Name);
        Assert.Equal("Season 2", seasons[1].Name);
    }
}
