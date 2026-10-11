using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using XtreamPlayer.Application;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Core.Library;
using XtreamPlayer.Infrastructure.Sports;
using XtreamPlayer.Infrastructure.TvMaze;
using XtreamPlayer.Server;

var builder = WebApplication.CreateBuilder(args);
var options = builder.Configuration.GetSection("Server").Get<ServerOptions>() ?? new();
options.DataDirectory = Path.GetFullPath(options.DataDirectory);
if (options.PairingCode.Length < 32) throw new InvalidOperationException("Set Server__PairingCode to a random secret of at least 32 characters.");
if (!Uri.TryCreate(options.PublicUrl, UriKind.Absolute, out var publicUri) ||
    publicUri.Scheme is not ("http" or "https") || (publicUri.Scheme != "https" && !publicUri.IsLoopback) ||
    !string.IsNullOrEmpty(publicUri.UserInfo) || publicUri.Query.Length > 0 || publicUri.Fragment.Length > 0) throw new InvalidOperationException("Server__PublicUrl must use HTTPS (except localhost).");
if (options.MaxTranscodes < 1 || options.MaxMediaBytes < 1 || options.MinimumFreeBytes < 0)
    throw new InvalidOperationException("Server media limits must be positive.");
// Playback sessions cannot survive a process restart; retain only persistent jobs.
var oldSessions = Path.Combine(options.MediaDirectory, "sessions");
if (Directory.Exists(oldSessions)) Directory.Delete(oldSessions, recursive: true);
builder.Services.AddSingleton(options);
builder.Services.AddDataProtection().SetApplicationName("XtreamPlayer.Server")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(options.DataDirectory, "keys")));
builder.Services.AddSingleton<ServerStore>();
builder.Services.AddTransient<SafeUpstream>();
builder.Services.AddHttpClient("upstream", c => c.Timeout = TimeSpan.FromMinutes(15))
    .ConfigurePrimaryHttpMessageHandler(SafeUpstream.Transport).AddHttpMessageHandler<SafeUpstream>().RemoveAllLoggers();
builder.Services.AddSingleton<CatalogService>();
builder.Services.AddSingleton<MediaTickets>();
builder.Services.AddSingleton<MediaEngine>();
builder.Services.AddSingleton<JobService>();
builder.Services.AddHostedService(s => s.GetRequiredService<JobService>());
builder.Services.AddHostedService<ServerMaintenance>();
builder.Services.AddTvMazeHttp();
builder.Services.AddSingleton<IAiringScheduleService>(s => new AiringScheduleService(
    s.GetRequiredService<IHttpClientFactory>(), s.GetRequiredService<ILogger<AiringScheduleService>>(), Path.Combine(options.DataDirectory, "cache")));
builder.Services.AddEspnHttp();
builder.Services.Replace(ServiceDescriptor.Singleton<ISportsScheduleService>(s => new SportsScheduleService(
    s.GetRequiredService<IHttpClientFactory>(), s.GetRequiredService<ILogger<SportsScheduleService>>(),
    s.GetRequiredService<ISportsDbKeyStore>(), Path.Combine(options.DataDirectory, "cache"))));
builder.Services.Replace(ServiceDescriptor.Singleton<ISportsDbKeyStore, ServerSportsKey>());
builder.Services.AddSingleton<DiscoveryService>();
builder.Services.AddCors(c => c.AddDefaultPolicy(p => p.WithOrigins(options.AllowedOrigins)
    .AllowAnyMethod().AllowAnyHeader().WithExposedHeaders("Content-Length", "Content-Range", "ETag")));
builder.Services.AddRateLimiter(c => c.AddPolicy("pair", context => RateLimitPartition.GetFixedWindowLimiter(
    context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
    { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.None);
var app = builder.Build();
app.UseCors();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers.CacheControl = "no-store";
    try
    {
        if (context.Request.Path.StartsWithSegments("/api/v1") && context.Request.Path != "/api/v1/pair")
        {
            var auth = context.Request.Headers.Authorization.ToString();
            var device = auth.StartsWith("Bearer ", StringComparison.Ordinal) ? context.RequestServices.GetRequiredService<ServerStore>().Authenticate(auth[7..]) : null;
            if (device is null) throw new UnauthorizedAccessException();
            context.Items["device"] = device;
        }
        await next();
    }
    catch (Exception e) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = e switch { UnauthorizedAccessException => 401, KeyNotFoundException => 404,
            ArgumentException => 400, InvalidOperationException => 409, OperationCanceledException => 408, _ => 502 };
        // Never serialize provider exception messages: they can contain upstream credentials.
        var message = e is ArgumentException or InvalidOperationException ? e.Message :
            context.Response.StatusCode == 401 ? "Pair this device again." : "The request could not complete. Check the provider or server connection.";
        await context.Response.WriteAsJsonAsync(new { error = message });
    }
});
string Device(HttpContext c) => (string)c.Items["device"]!;
app.MapGet("/health", () => Results.Ok(new { status = "ok", apiVersion = 1 }));
app.MapPost("/api/v1/pair", (PairRequest request, ServerStore store) =>
{
    if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(ServerStore.Hash(request.Code ?? "")),
        Convert.FromHexString(ServerStore.Hash(options.PairingCode)))) return Results.Unauthorized();
    var token = ServerStore.Token();
    var id = ServerStore.Hash(token);
    store.Put("device", id, new ServerStore.DeviceRecord(id, (request.DeviceName ?? "iPhone")[..Math.Min(request.DeviceName?.Length ?? 6, 80)], DateTimeOffset.UtcNow));
    return Results.Ok(new PairResponse(token, id));
}).RequireRateLimiting("pair");
app.MapGet("/api/v1/devices", (ServerStore store) => store.List<ServerStore.DeviceRecord>("device").Select(d => new DeviceSummary(d.Id, d.Name, d.CreatedAt)));
app.MapDelete("/api/v1/devices/{id}", (string id, ServerStore store) => { store.Delete("device", id); return Results.NoContent(); });
app.MapGet("/api/v1/providers", (ServerStore store) => store.List<Profile>("profile").Select(p => new ProviderSummary(p.Id, p.Name, p.Status ?? "Unknown", p.MaxConnections)));
app.MapPost("/api/v1/providers", async (SaveProvider request, ServerStore store, CatalogService catalogs) =>
{
    var normalized = ServerUrl.Normalize(request.ServerUrl);
    if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
        throw new ArgumentException("Enter an HTTP(S) provider URL.");
    if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
        throw new ArgumentException("Name, username and password are required.");
    var profile = new Profile { Id = Guid.NewGuid().ToString("N"), Name = request.Name, ServerUrl = normalized,
        Username = request.Username, Password = request.Password, PreferredLiveFormat = LiveStreamFormat.Hls,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
    store.Put("profile", profile.Id, profile);
    try
    {
        var account = await catalogs.Client(profile.Id).AuthenticateAsync();
        profile = profile with { Status = account.Status, MaxConnections = account.MaxConnections };
        store.Put("profile", profile.Id, profile);
        return new ProviderSummary(profile.Id, profile.Name, account.Status, account.MaxConnections);
    }
    catch { store.Delete("profile", profile.Id); throw; }
});
app.MapDelete("/api/v1/providers/{id}", (string id, ServerStore store, JobService jobs, MediaEngine media) =>
{
    if (jobs.List().Any(j => j.ProviderId == id && j.State is not ("Completed" or "Cancelled" or "Failed" or "Interrupted")))
        throw new InvalidOperationException("Stop or delete provider jobs before removing the provider.");
    media.StopProvider(id);
    store.Delete("profile", id); store.DeletePrefix("cache", id + ":"); store.DeletePrefix("artwork", id + ".");
    return Results.NoContent();
});
app.MapGet("/api/v1/providers/{p}/catalog/{kind}", (string p, string kind, bool? refresh, CatalogService catalogs) => catalogs.Browse(p, kind, refresh ?? false));
app.MapGet("/api/v1/providers/{p}/detail/{kind}/{id}", (string p, string kind, string id, CatalogService catalogs) => catalogs.Detail(p, kind, id));
app.MapGet("/api/v1/providers/{p}/discovery", (string p, bool? refresh, DiscoveryService discovery) => discovery.Get(p, refresh ?? false));
app.MapGet("/api/v1/providers/{p}/guide", async (string p, DateTimeOffset from, DateTimeOffset to, bool? refresh, bool? sports, bool? names, CatalogService catalogs, ISportsScheduleService schedules) =>
{
    if (to <= from || to - from > TimeSpan.FromDays(2)) throw new ArgumentException("Guide window must be within two days.");
    Dictionary<string, List<EpgProgramme>> guide;
    try { guide = await catalogs.Guide(p, from, to, refresh ?? false); }
    catch { guide = []; }
    if (sports == true) await schedules.RefreshIfStaleAsync();
    if (names != false)
        foreach (var channel in (await catalogs.Live(p)).Items)
            if (!guide.TryGetValue(channel.Id, out var entries) || entries.Count == 0)
                guide[channel.Id] = ChannelNameSchedule.Programmes(channel, from, to, DateTimeOffset.UtcNow, sports == true ? schedules.Matcher : null).ToList();
    return guide;
});
app.MapGet("/api/v1/providers/{p}/guide/{id}", async (string p, string id, CatalogService catalogs) => await catalogs.Client(p).GetShortEpgAsync(id));
app.MapPost("/api/v1/settings/sports-key", async (SportsKeyRequest r, ISportsDbKeyStore keys) => { await keys.SetAsync(r.Key); return Results.NoContent(); });
app.MapGet("/api/v1/artwork/{id}", async (string id, ServerStore store, IHttpClientFactory clients, HttpContext c) =>
{
    var url = store.Get<string>("artwork", id) ?? throw new KeyNotFoundException();
    using var response = await clients.CreateClient("upstream").GetAsync(url, HttpCompletionOption.ResponseHeadersRead, c.RequestAborted);
    response.EnsureSuccessStatusCode();
    var type = response.Content.Headers.ContentType?.MediaType;
    if (type is not ("image/jpeg" or "image/png" or "image/webp" or "image/gif")) return Results.NotFound();
    await using var stream = await response.Content.ReadAsStreamAsync(c.RequestAborted);
    using var buffer = new MemoryStream(); var bytes = new byte[65536]; int read;
    while ((read = await stream.ReadAsync(bytes, c.RequestAborted)) > 0)
    { if (buffer.Length + read > 5 * 1024 * 1024) return Results.BadRequest(); buffer.Write(bytes, 0, read); }
    return Results.File(buffer.ToArray(), type);
});
app.MapPost("/api/v1/playback", (PlayRequest r, HttpContext c, MediaEngine engine) => engine.Open(r, Device(c), c.RequestAborted));
app.MapPost("/api/v1/playback/{id}/heartbeat", (string id, HttpContext c, MediaEngine engine) => { engine.Session(id, Device(c)); return Results.NoContent(); });
app.MapDelete("/api/v1/playback/{id}", (string id, HttpContext c, MediaEngine engine) => { engine.Session(id, Device(c)); engine.Close(id); return Results.NoContent(); });
app.MapGet("/api/v1/jobs", (JobService jobs) => jobs.List());
app.MapPost("/api/v1/jobs", (CreateJob r, HttpContext c, JobService jobs) => jobs.Create(r, Device(c)));
app.MapPost("/api/v1/jobs/{id}/{action}", (string id, string action, JobService jobs) =>
{
    if (action == "recover") jobs.Recover(id); else jobs.Change(id, action); return Results.NoContent();
});
app.MapPost("/api/v1/jobs/{id}/file", (string id, HttpContext c, ServerStore store, JobService jobs, MediaTickets tickets) =>
{
    var job = store.Get<MediaJob>("job", id) ?? throw new KeyNotFoundException();
    if (job.State != "Completed") throw new InvalidOperationException("The media is not ready.");
    var file = new FileInfo(jobs.FileFor(id));
    return new MediaFile(id, tickets.Public($"/media/files/{id}?t=" + Uri.EscapeDataString(tickets.Issue(Device(c), "file", id))),
        file.Length, $"{file.Length}-{file.LastWriteTimeUtc.Ticks}", job.Title);
});
app.MapGet("/media/relay/{name}", (string t, HttpContext c, MediaTickets tickets, IHttpClientFactory clients) => Relay.Serve(c, t, tickets, clients));
app.MapMethods("/media/files/{id}", ["GET", "HEAD"], (string id, string t, MediaTickets tickets, JobService jobs) =>
{
    if (tickets.Read(t, "file").Resource != id) throw new UnauthorizedAccessException();
    return Results.File(jobs.FileFor(id), "video/mp4", enableRangeProcessing: true);
});
app.MapGet("/media/sessions/{id}/{file}", async (string id, string file, string t, HttpContext c, MediaTickets tickets, MediaEngine engine) =>
{
    var grant = tickets.Read(t, "session");
    if (grant.Resource != id) throw new UnauthorizedAccessException();
    var session = engine.Session(id, grant.Device);
    if (!System.Text.RegularExpressions.Regex.IsMatch(file, "^(?:(?:index|master|index_vtt)\\.m3u8|index[0-9]+\\.vtt|segment-[0-9]+\\.ts)$")) throw new KeyNotFoundException();
    var path = Path.Combine(session.Directory, file);
    if (file.EndsWith(".m3u8", StringComparison.Ordinal))
    {
        var manifest = await File.ReadAllTextAsync(path, c.RequestAborted);
        manifest = Relay.Rewrite(manifest, new Uri("https://generated.invalid/"),
            url => Path.GetFileName(new Uri(url).AbsolutePath) + "?t=" + Uri.EscapeDataString(t));
        return Results.Text(manifest, "application/vnd.apple.mpegurl");
    }
    return Results.File(path, file.EndsWith(".vtt", StringComparison.Ordinal) ? "text/vtt" : "video/mp2t", enableRangeProcessing: true);
});
app.Run();
public sealed record SportsKeyRequest(string? Key);
public partial class Program { }
