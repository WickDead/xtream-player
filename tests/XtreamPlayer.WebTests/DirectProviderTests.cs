using System.Net;
using System.Text;
using System.Text.Json;
using XtreamPlayer.Application;
using Xunit;

namespace XtreamPlayer.WebTests;

public sealed class DirectProviderTests
{
    [Fact]
    public async Task DirectCatalogAndNativePlaybackUseProviderUrlsAndPersistLogin()
    {
        var storage = new MemoryStorage();
        var transport = new Fixture();
        var service = new DirectProviderClient(new HttpClient(transport), storage);
        await service.Initialize();
        var provider = await service.Add(new("Test provider", "https://provider.example", "user/name", "p?ss"));
        var live = await service.Browse(provider.Id, "live");
        Assert.Single(live.Items);
        var playback = await service.OpenAsync(new(provider.Id, "live", "1"));
        Assert.Equal("https://provider.example/live/user%2Fname/p%3Fss/1.m3u8", playback.Url);
        Assert.False(playback.Capabilities.AudioSelection);
        var movie = await service.OpenAsync(new(provider.Id, "vod", "7", PositionSeconds: 120));
        Assert.EndsWith("/7.mp4", movie.Url);
        Assert.Equal(0, movie.OffsetSeconds); // Resume seeks in the browser, never adds a server offset.
        var detail = await service.Detail(provider.Id, "series", "10");
        Assert.Single(detail.Episodes);
        var episode = await service.OpenAsync(new(provider.Id, "episode", "11", "10"));
        Assert.Equal("https://cdn.example/episode.mp4", episode.Url);
        Assert.All(transport.Requests, u => Assert.Equal("provider.example", u.Host));
        Assert.DoesNotContain("p?ss", JsonSerializer.Serialize(live));
        var reload = new DirectProviderClient(new HttpClient(new Fixture()), storage);
        await reload.Initialize();
        Assert.Equal(provider.Id, Assert.Single(reload.Providers()).Id);
        await reload.Remove(provider.Id);
        await service.Initialize();
        Assert.Empty(service.Providers());
    }
    [Fact]
    public async Task RejectedProviderIsNotSaved()
    {
        var storage = new MemoryStorage();
        var service = new DirectProviderClient(new HttpClient(new Fixture { Deny = true }), storage);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.Add(new("Test", "https://provider.example", "user", "secret")));
        Assert.DoesNotContain("secret", error.Message);
        Assert.Empty(service.Providers());
        Assert.Empty(storage.Values);
    }
    [Theory]
    [InlineData("http://provider.example")]
    [InlineData("https://user:secret@provider.example")]
    [InlineData("javascript:alert(1)")]
    public void HostedBrowserRejectsInsecureOrNonWebMedia(string url) => Assert.Throws<InvalidOperationException>(() => DirectProviderClient.BrowserUrl(url));

    private sealed class MemoryStorage : IDeviceStorage
    {
        public Dictionary<string, string> Values { get; } = [];
        public ValueTask<T?> GetAsync<T>(string key) => ValueTask.FromResult(Values.TryGetValue(key, out var value) ? JsonSerializer.Deserialize<T>(value) : default);
        public ValueTask SetAsync<T>(string key, T value) { Values[key] = JsonSerializer.Serialize(value); return ValueTask.CompletedTask; }
        public ValueTask RemoveAsync(string key) { Values.Remove(key); return ValueTask.CompletedTask; }
    }
    private sealed class Fixture : HttpMessageHandler
    {
        public bool Deny { get; init; }
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            if (Deny) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            var query = request.RequestUri!.Query;
            var body = query.Contains("get_live_categories") ? """[{"category_id":"1","category_name":"Live"}]""" :
                query.Contains("get_live_streams") ? """[{"stream_id":1,"name":"Test channel","category_id":"1"}]""" :
                query.Contains("get_vod_categories") ? """[{"category_id":"2","category_name":"Movies"}]""" :
                query.Contains("get_vod_streams") ? """[{"stream_id":7,"name":"Test movie","category_id":"2","container_extension":"mp4"}]""" :
                query.Contains("get_series_info") ? """{"info":{"name":"Test show"},"episodes":{"1":[{"id":"11","title":"Pilot","episode_num":1,"season":1,"container_extension":"mp4","direct_source":"https://cdn.example/episode.mp4"}]}}""" :
                """{"user_info":{"status":"Active","max_connections":"2","allowed_output_formats":["ts"]},"server_info":{}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
