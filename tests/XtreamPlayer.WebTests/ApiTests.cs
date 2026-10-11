using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XtreamPlayer.Application;
using XtreamPlayer.Server;
using Xunit;

namespace XtreamPlayer.WebTests;

public sealed class ApiTests
{
    private const string PairCode = "test-only-pairing-code-not-a-real-secret";

    [Fact]
    public async Task PairingRevocationAndPrivateRoutes()
    {
        await using var app = new Factory();
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/providers")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/pair", new PairRequest("wrong", "phone"))).StatusCode);
        var pair = await Pair(client);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/providers")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/devices/" + pair.DeviceId)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/providers")).StatusCode);
    }

    [Fact]
    public async Task ProviderCatalogNeverReturnsPasswordOrStreamUrl()
    {
        await using var app = new Factory();
        using var client = app.CreateClient();
        await Pair(client);
        using var response = await client.PostAsJsonAsync("/api/v1/providers",
            new SaveProvider("Fixture", "https://provider.example", "fixture-user", "fixture-password"));
        response.EnsureSuccessStatusCode();
        var provider = (await response.Content.ReadFromJsonAsync<ProviderSummary>())!;
        var json = await client.GetStringAsync($"/api/v1/providers/{provider.Id}/catalog/vod");
        Assert.DoesNotContain("fixture-password", json);
        Assert.DoesNotContain("fixture-user", json);
        Assert.DoesNotContain("streamUrl", json);
        Assert.Contains("Fixture Movie", json);
        using var details = await client.GetAsync($"/api/v1/providers/{provider.Id}/detail/vod/7");
        Assert.True(details.IsSuccessStatusCode);
    }

    [Fact]
    public async Task MediaFilesHaveScopedRevocableTicketsAndByteRanges()
    {
        await using var app = new Factory();
        using var client = app.CreateClient();
        var pair = await Pair(client);
        var store = app.Services.GetRequiredService<ServerStore>();
        var jobs = app.Services.GetRequiredService<JobService>();
        var id = Guid.NewGuid().ToString("N");
        var job = new MediaJob(id, "provider", "Test", "Completed", false, 100, 100, null, DateTimeOffset.UtcNow, new("provider", "vod", "7"));
        store.Put("job", id, job);
        Directory.CreateDirectory(jobs.DirectoryFor(id));
        await File.WriteAllBytesAsync(jobs.FileFor(id), Enumerable.Range(0, 100).Select(i => (byte)i).ToArray());
        var grantResponse = await client.PostAsJsonAsync("/api/v1/jobs/" + id + "/file", new { });
        grantResponse.EnsureSuccessStatusCode();
        var file = (await grantResponse.Content.ReadFromJsonAsync<MediaFile>())!;
        using var range = new HttpRequestMessage(HttpMethod.Get, new Uri(file.Url).PathAndQuery);
        range.Headers.Range = new RangeHeaderValue(10, 19);
        using var response = await client.SendAsync(range);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(Enumerable.Range(10, 10).Select(i => (byte)i), await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("bytes 10-19/100", response.Content.Headers.ContentRange!.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(new Uri(file.Url).PathAndQuery.Replace(id, Guid.NewGuid().ToString("N")))).StatusCode);
        await client.DeleteAsync("/api/v1/devices/" + pair.DeviceId);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(new Uri(file.Url).PathAndQuery)).StatusCode);
    }

    [Fact]
    public async Task CorsAllowsConfiguredPagesOriginOnly()
    {
        await using var app = new Factory();
        using var client = app.CreateClient();
        using var good = new HttpRequestMessage(HttpMethod.Options, "/api/v1/providers");
        good.Headers.Add("Origin", "https://owner.github.io");
        good.Headers.Add("Access-Control-Request-Method", "GET");
        var response = await client.SendAsync(good);
        Assert.Equal("https://owner.github.io", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        using var bad = new HttpRequestMessage(HttpMethod.Options, "/api/v1/providers");
        bad.Headers.Add("Origin", "https://untrusted.example");
        bad.Headers.Add("Access-Control-Request-Method", "GET");
        var denied = await client.SendAsync(bad);
        Assert.False(denied.Headers.Contains("Access-Control-Allow-Origin"));
    }

    private static async Task<PairResponse> Pair(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/pair", new PairRequest(PairCode, "Test iPhone"));
        response.EnsureSuccessStatusCode();
        var pair = (await response.Content.ReadFromJsonAsync<PairResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pair.Token);
        return pair;
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "xtream-api-tests-" + Guid.NewGuid().ToString("N"));
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Server:PairingCode", PairCode);
            builder.UseSetting("Server:PublicUrl", "http://localhost");
            builder.UseSetting("Server:DataDirectory", _directory);
            builder.UseSetting("Server:AllowedOrigins:0", "https://owner.github.io");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureServices(services => services.AddHttpClient("upstream").ConfigurePrimaryHttpMessageHandler(() => new ProviderFixture()));
        }
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }
    private sealed class ProviderFixture : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri!.Query;
            var body = query.Contains("get_vod_categories") ? """[{"category_id":"1","category_name":"Movies"}]""" :
                query.Contains("get_vod_streams") ? """[{"stream_id":7,"name":"Fixture Movie","category_id":"1","container_extension":"mkv"}]""" :
                query.Contains("get_vod_info") ? """{"info":{"name":"Fixture Movie","plot":"A test film"},"movie_data":{"stream_id":7,"name":"Fixture Movie"}}""" :
                """{"user_info":{"status":"Active","max_connections":"2","allowed_output_formats":["m3u8","ts"]},"server_info":{}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json"), RequestMessage = request });
        }
    }
}
