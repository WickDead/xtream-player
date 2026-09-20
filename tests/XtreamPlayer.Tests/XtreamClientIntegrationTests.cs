using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Infrastructure.Xtream;
using Xunit;
using Xunit.Abstractions;

namespace XtreamPlayer.Tests;

/// <summary>
/// Serializes every test that talks to a real provider.
/// </summary>
/// <remarks>
/// Providers cap concurrent connections — commonly 1 or 2. xunit parallelizes
/// test classes by default, and six tests each pulling a multi-megabyte catalog
/// exhausts the limit: the first symptom was a stream request timing out after
/// 32 seconds because no slot was free. This is the same constraint CatalogStore
/// respects by fetching catalogs sequentially rather than in parallel.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProviderCollection
{
    public const string Name = "real-provider";
}

/// <summary>
/// Exercises the client against a real provider.
/// </summary>
/// <remarks>
/// <para>
/// Opt-in. Set <c>XP_LEGACY_DB</c> to a database containing a provider profile
/// and these run against the first active one; otherwise they skip.
/// </para>
/// <para>
/// Fixtures cannot substitute for this. The whole point of the lenient converter
/// layer is surviving what real servers send, and only a real server proves it.
/// </para>
/// </remarks>
[Collection(ProviderCollection.Name)]
public sealed class XtreamClientIntegrationTests(ITestOutputHelper output)
{
    private static string? LegacyPath
    {
        get
        {
            var path = Environment.GetEnvironmentVariable("XP_LEGACY_DB");
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
        }
    }

    /// <summary>
    /// Imports the legacy database into a throwaway one and returns a client for
    /// the profile with the most favorites, which is the one actually in use.
    /// </summary>
    private static async Task<(XtreamClient Client, TempDatabase Db)?> CreateClientAsync()
    {
        if (LegacyPath is null)
        {
            return null;
        }

        var db = new TempDatabase();
        var importer = new XtreamPlayer.Infrastructure.Migration.LegacyImporter(
            db.Connections, db.Protector);

        var copy = Path.Combine(Path.GetTempPath(), "xp-int-" + Guid.NewGuid().ToString("N") + ".sqlite");
        File.Copy(LegacyPath!, copy);

        await importer.ImportAsync(copy);
        File.Delete(copy);

        var profiles = await db.Profiles.ListAsync();
        if (profiles.Count == 0)
        {
            db.Dispose();
            return null;
        }

        Profile? best = null;
        var bestCount = -1;

        foreach (var profile in profiles)
        {
            var count = (await db.Favorites.ListAsync(new ProfileScope(profile.Id))).Count;
            if (count > bestCount)
            {
                bestCount = count;
                best = profile;
            }
        }

        var services = new ServiceCollection();
        services.AddXtreamHttp();
        var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IXtreamClientFactory>();
        return (factory.For(best!), db);
    }

    [SkippableFact]
    public async Task AuthenticatesAndReportsAccountState()
    {
        var context = await CreateClientAsync();
        Skip.If(context is null, "Set XP_LEGACY_DB to run this.");
        using var db = context!.Value.Db;

        var account = await context.Value.Client.AuthenticateAsync();

        output.WriteLine($"status   : {account.Status}");
        output.WriteLine($"expires  : {account.ExpiresAt:yyyy-MM-dd}");
        output.WriteLine($"conns    : {account.ActiveConnections}/{account.MaxConnections}");
        output.WriteLine($"formats  : {string.Join(", ", account.AllowedOutputFormats)}");

        Assert.True(account.IsActive);
        Assert.NotEmpty(account.AllowedOutputFormats);
    }

    [SkippableFact]
    public async Task LoadsTheLiveCatalogAndBuildsPlayableUrls()
    {
        var context = await CreateClientAsync();
        Skip.If(context is null, "Set XP_LEGACY_DB to run this.");
        using var db = context!.Value.Db;

        var stopwatch = Stopwatch.StartNew();
        var catalog = await context.Value.Client.GetLiveCatalogAsync();
        stopwatch.Stop();

        output.WriteLine($"{catalog.Items.Count:N0} channels, "
                       + $"{catalog.Categories.Count} categories, {stopwatch.ElapsedMilliseconds} ms");

        Assert.NotEmpty(catalog.Items);
        Assert.NotEmpty(catalog.Categories);

        // Every required field must be populated; a null here breaks the UI.
        foreach (var channel in catalog.Items)
        {
            Assert.False(string.IsNullOrWhiteSpace(channel.Id));
            Assert.False(string.IsNullOrWhiteSpace(channel.Name));
            Assert.False(string.IsNullOrWhiteSpace(channel.StreamUrl));
        }

        // The provider's own ordering must survive, since a sort mode uses it.
        Assert.Equal(
            catalog.Categories.Select(c => c.Order).Order().ToArray(),
            catalog.Categories.Select(c => c.Order).ToArray());

        // ts is preferred, and is exactly what the old webview player could not
        // decode.
        var sample = catalog.Items[0];
        output.WriteLine($"sample   : {sample.Name} -> ...{sample.StreamUrl[^12..]}");
        Assert.Matches(@"\.(ts|m3u8)$", sample.StreamUrl);
    }

    [SkippableFact]
    public async Task LoadsTheVodCatalogAtScale()
    {
        var context = await CreateClientAsync();
        Skip.If(context is null, "Set XP_LEGACY_DB to run this.");
        using var db = context!.Value.Db;

        var before = GC.GetTotalAllocatedBytes();
        var stopwatch = Stopwatch.StartNew();
        var catalog = await context.Value.Client.GetVodCatalogAsync();
        stopwatch.Stop();
        var allocated = (GC.GetTotalAllocatedBytes() - before) / 1024d / 1024d;

        output.WriteLine($"{catalog.Items.Count:N0} movies, "
                       + $"{catalog.Categories.Count} categories, "
                       + $"{stopwatch.ElapsedMilliseconds} ms, ~{allocated:F0} MB allocated");

        Assert.NotEmpty(catalog.Items);

        // Ratings must be normalized, never raw provider junk.
        foreach (var item in catalog.Items.Where(i => i.Rating is not null).Take(200))
        {
            Assert.Matches(@"^\d+(\.\d)?$", item.Rating!);
        }

        var withPosters = catalog.Items.Count(i => i.PosterUrl is not null);
        output.WriteLine($"posters  : {withPosters:N0} of {catalog.Items.Count:N0}");
    }

    [SkippableFact]
    public async Task LoadsSeriesAndResolvesEpisodes()
    {
        var context = await CreateClientAsync();
        Skip.If(context is null, "Set XP_LEGACY_DB to run this.");
        using var db = context!.Value.Db;

        var client = context.Value.Client;
        var catalog = await client.GetSeriesCatalogAsync();

        output.WriteLine($"{catalog.Items.Count:N0} series");
        Assert.NotEmpty(catalog.Items);

        // Detail for a handful, to exercise season inference against real data.
        var checkedSeries = 0;

        foreach (var series in catalog.Items.Take(3))
        {
            var detail = await client.GetSeriesDetailAsync(series.Id);
            if (detail is null)
            {
                continue;
            }

            checkedSeries++;
            output.WriteLine(
                $"  {detail.Name}: {detail.Seasons.Count} season(s), {detail.EpisodeCount} episode(s)");

            // Every season with episodes must be present in the season list -
            // the case real providers get wrong by returning an empty seasons
            // array alongside a full episode map.
            foreach (var seasonNumber in detail.EpisodesBySeason.Keys)
            {
                Assert.Contains(detail.Seasons, s => s.Number == seasonNumber);
            }

            foreach (var episode in detail.EpisodesBySeason.Values.SelectMany(e => e).Take(5))
            {
                Assert.False(string.IsNullOrWhiteSpace(episode.StreamUrl));
                Assert.StartsWith("http", episode.StreamUrl, StringComparison.Ordinal);
            }
        }

        Assert.True(checkedSeries > 0, "No series detail could be loaded.");
    }

    [SkippableFact]
    public async Task ResolvedStreamUrlsActuallyServeMedia()
    {
        var context = await CreateClientAsync();
        Skip.If(context is null, "Set XP_LEGACY_DB to run this.");
        using var db = context!.Value.Db;

        var catalog = await context.Value.Client.GetLiveCatalogAsync();
        var channel = catalog.Items[0];

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd(XtreamHttpRegistration.UserAgent);

        using var request = new HttpRequestMessage(HttpMethod.Get, channel.StreamUrl);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 8191);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        output.WriteLine($"{channel.Name}: {(int)response.StatusCode} "
                       + $"{response.Content.Headers.ContentType}");

        // A 407 means the provider dropped that channel, which is real and not a
        // client bug; the catalog itself is still correct.
        Skip.If(response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired,
            "The provider no longer serves this channel.");

        Assert.True(response.IsSuccessStatusCode, $"Expected success, got {response.StatusCode}.");

        // Read a fixed prefix, never the whole body. A live TS stream has no
        // end: ReadAsByteArrayAsync waits for one and the request simply times
        // out, even though the stream is working perfectly. Providers also
        // ignore Range on live endpoints, so the header cannot be relied on to
        // bound the response.
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        var buffer = new byte[8192];
        var read = await stream.ReadAtLeastAsync(
            buffer, 376, throwOnEndOfStream: false, cts.Token);

        var bytes = buffer.AsSpan(0, read).ToArray();
        Assert.NotEmpty(bytes);

        // MPEG-TS packets are 188 bytes and start with sync byte 0x47.
        if (channel.StreamUrl.EndsWith(".ts", StringComparison.Ordinal) && bytes.Length > 376)
        {
            Assert.Equal(0x47, bytes[0]);
            Assert.Equal(0x47, bytes[188]);
            output.WriteLine("valid MPEG-TS sync");
        }
    }

    [SkippableFact]
    public async Task ShortEpgDecodesProgrammeTitles()
    {
        var context = await CreateClientAsync();
        Skip.If(context is null, "Set XP_LEGACY_DB to run this.");
        using var db = context!.Value.Db;

        var client = context.Value.Client;
        var catalog = await client.GetLiveCatalogAsync();

        // Channels carrying an EPG id are the ones likely to return listings.
        var withEpg = catalog.Items.Where(c => !string.IsNullOrWhiteSpace(c.EpgChannelId)).Take(5);
        var found = 0;

        foreach (var channel in withEpg)
        {
            var entries = await client.GetShortEpgAsync(channel.Id, limit: 4);
            if (entries.Count == 0)
            {
                continue;
            }

            found += entries.Count;
            output.WriteLine($"{channel.Name}:");

            foreach (var entry in entries)
            {
                output.WriteLine($"  {entry.Start:HH:mm}-{entry.Stop:HH:mm}  {entry.Title}");

                Assert.False(string.IsNullOrWhiteSpace(entry.Title));
                Assert.True(entry.Stop > entry.Start, "A programme must end after it starts.");

                // Base64 that failed to decode would look like base64.
                Assert.DoesNotContain("==", entry.Title, StringComparison.Ordinal);
            }

            break;
        }

        Skip.If(found == 0, "This provider returned no short EPG data.");
    }
}
