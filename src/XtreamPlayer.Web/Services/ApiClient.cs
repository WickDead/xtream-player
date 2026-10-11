using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using XtreamPlayer.Application;
namespace XtreamPlayer.Web.Services;
public sealed class ApiClient(BrowserStore storage) : IRemotePlayback, IMediaJobs
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(15) };
    public Connection? Connection { get; private set; }
    public string CachePrefix => "server:" + Connection?.Url;
    public async Task Initialize() => Connection = await storage.GetAsync<Connection>("connection");
    public async Task Pair(string url, string code)
    {
        url = url.Trim().TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || (uri.Scheme != "https" && !uri.IsLoopback) ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException("Enter the HTTPS address of your backend.");
        using var response = await _http.PostAsJsonAsync(url + "/api/v1/pair", new PairRequest(code, "iPhone PWA"));
        await Check(response);
        var pair = await response.Content.ReadFromJsonAsync<PairResponse>() ?? throw new InvalidOperationException("Pairing failed.");
        Connection = new(url, pair.Token, pair.DeviceId);
        await storage.SetAsync("connection", Connection);
    }
    public async Task Forget() { Connection = null; await storage.RemoveAsync("connection"); }
    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        var connection = Connection ?? throw new InvalidOperationException("Connect to your server first.");
        using var request = new HttpRequestMessage(method, connection.Url + "/api/v1/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.Token);
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await _http.SendAsync(request, ct);
        try { await Check(response); return response; } catch { response.Dispose(); throw; }
    }
    private static async Task Check(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var message = "The server could not complete the request.";
        try { var error = await response.Content.ReadFromJsonAsync<JsonElement>(); message = error.GetProperty("error").GetString() ?? message; } catch { }
        throw new InvalidOperationException(response.StatusCode == System.Net.HttpStatusCode.Unauthorized ? "Device pairing expired or was revoked. Pair again in Settings." : message);
    }
    public async Task<T> Get<T>(string path)
    { using var r = await Send(HttpMethod.Get, path); return (await r.Content.ReadFromJsonAsync<T>())!; }
    public async Task<T> Post<T>(string path, object body)
    { using var r = await Send(HttpMethod.Post, path, body); return (await r.Content.ReadFromJsonAsync<T>())!; }
    public async Task Post(string path, object body)
    { using var r = await Send(HttpMethod.Post, path, body); }
    public async Task Delete(string path) { using var r = await Send(HttpMethod.Delete, path); }
    public Task<PlaybackSession> OpenAsync(PlayRequest r, CancellationToken ct = default) => Post<PlaybackSession>("playback", r);
    public Task CloseAsync(string id, CancellationToken ct = default) => Delete("playback/" + id);
    public async Task<IReadOnlyList<MediaJob>> ListAsync(CancellationToken ct = default) => await Get<List<MediaJob>>("jobs");
    public Task<MediaJob> CreateAsync(CreateJob r, CancellationToken ct = default) => Post<MediaJob>("jobs", r);
    public Task ChangeAsync(string id, string action, CancellationToken ct = default) => Post($"jobs/{id}/{action}", new { });
}
public sealed record Connection(string Url, string Token, string DeviceId);
public sealed record OfflineItem(string Id, string Title, long Bytes, long Total, bool Complete, string? Error, MediaItem? Item, string? ProviderId, string? ServerUrl);
