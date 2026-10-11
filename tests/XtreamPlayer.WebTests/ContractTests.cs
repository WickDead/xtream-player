using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using XtreamPlayer.Application;
using XtreamPlayer.Core.Library;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Server;
using Xunit;

namespace XtreamPlayer.WebTests;

public sealed class ContractTests
{
    [Theory]
    [InlineData("127.0.0.1", false)] [InlineData("10.10.1.1", false)] [InlineData("169.254.169.254", false)]
    [InlineData("172.16.0.1", false)] [InlineData("192.168.1.1", false)] [InlineData("100.64.0.1", false)]
    [InlineData("::1", false)] [InlineData("::ffff:127.0.0.1", false)] [InlineData("fc00::1", false)]
    [InlineData("fe80::1", false)] [InlineData("224.0.0.1", false)] [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void UpstreamConnectionsCannotReachPrivateNetworks(string address, bool allowed) =>
        Assert.Equal(allowed, SafeUpstream.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public void BothFrontendsUseTheSameFilterSemantics()
    {
        MediaItem[] items = [new("1", "Zulu", "hidden", 0, "vod"), new("2", "Alpha", "shown", 1, "vod"), new("3", "Bravo", "shown", 1, "vod")];
        var workflow = new BrowseWorkflow<MediaItem>(items, (x, i) => new(x.Id, x.Name, x.CategoryId, x.CategoryOrder, x.Added, i));
        var request = new FilterRequest { Selection = CategorySelection.All.Instance, HiddenCategoryIds = new HashSet<string> { "hidden" }, Sort = ItemSort.Name };
        Assert.Equal(["2", "3"], workflow.Apply(request).Select(x => x.Id));
        Assert.Equal(["1"], workflow.Apply(request with { Selection = CategorySelection.Favorites.Instance, FavoriteIds = new HashSet<string> { "1" } }).Select(x => x.Id));
        Assert.Equal(["3"], workflow.Apply(request with { SearchText = "br" }).Select(x => x.Id));
    }
    [Fact]
    public void RelayRewritesKeysVariantsSegmentsAndAlternateTracks()
    {
        var manifest = """
            #EXTM3U
            #EXT-X-KEY:METHOD=AES-128,URI="key.bin"
            #EXT-X-MEDIA:TYPE=AUDIO,URI="/audio/main.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=800000
            high/index.m3u8
            https://cdn.example/segment.ts
            """;
        var urls = new List<string>();
        var result = Relay.Rewrite(manifest, new Uri("https://provider.example/live/index.m3u8"), url => { urls.Add(url); return "/guarded/" + urls.Count; });
        Assert.Contains("https://provider.example/live/key.bin", urls);
        Assert.Contains("https://provider.example/audio/main.m3u8", urls);
        Assert.Contains("https://provider.example/live/high/index.m3u8", urls);
        Assert.Contains("https://cdn.example/segment.ts", urls);
        Assert.DoesNotContain("https://", result);
        Assert.Throws<InvalidOperationException>(() => Relay.Rewrite("not a playlist", new Uri("https://example.com"), x => x));
    }
    [Fact]
    public void PublicCatalogContractsDoNotCarryProviderCredentials()
    {
        var json = JsonSerializer.Serialize(new MediaItem("1", "Movie", "2", 0, "vod"));
        Assert.DoesNotContain("StreamUrl", json);
        Assert.DoesNotContain("Password", json);
        Assert.DoesNotContain("Username", json);
    }
    [Fact]
    public void EncryptedPersistenceTicketsAndRevocationSurviveRecreation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xtream-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var options = new ServerOptions { DataDirectory = directory };
            var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory, "keys")));
            var store = new ServerStore(options, protection);
            var token = ServerStore.Token();
            var device = ServerStore.Hash(token);
            store.Put("device", device, new ServerStore.DeviceRecord(device, "Test phone", DateTimeOffset.UtcNow));
            store.Put("secret", "provider", "never-store-this-plaintext");
            Assert.Equal(device, store.Authenticate(token));
            var tickets = new MediaTickets(protection, store, options);
            var signed = tickets.Issue(device, "file", "movie");
            Assert.Equal("movie", tickets.Read(signed, "file").Resource);
            Assert.Throws<UnauthorizedAccessException>(() => tickets.Read(signed, "relay"));
            Assert.Throws<UnauthorizedAccessException>(() => tickets.Read(signed + "tampered", "file"));
            Assert.Throws<UnauthorizedAccessException>(() => tickets.Read(tickets.Issue(device, "file", "movie", TimeSpan.FromSeconds(-1)), "file"));
            var reopened = new ServerStore(options, protection);
            Assert.Equal("never-store-this-plaintext", reopened.Get<string>("secret", "provider"));
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var contents = File.ReadAllText(Path.Combine(directory, "server.sqlite"));
            Assert.DoesNotContain("never-store-this-plaintext", contents);
            store.Delete("device", device);
            Assert.Null(store.Authenticate(token));
            Assert.Throws<UnauthorizedAccessException>(() => tickets.Read(signed, "file"));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }
}
