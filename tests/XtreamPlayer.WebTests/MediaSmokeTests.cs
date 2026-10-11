using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XtreamPlayer.Application;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Server;
using Xunit;

namespace XtreamPlayer.WebTests;

public sealed class MediaSmokeTests
{
    [SkippableFact]
    public async Task RealFfmpegProducesPlayableHlsSubtitlesAndOfflineMp4()
    {
        var ffmpeg = Environment.GetEnvironmentVariable("XTREAM_TEST_FFMPEG");
        var ffprobe = Environment.GetEnvironmentVariable("XTREAM_TEST_FFPROBE");
        Skip.If(string.IsNullOrEmpty(ffmpeg) || string.IsNullOrEmpty(ffprobe), "Set XTREAM_TEST_FFMPEG and XTREAM_TEST_FFPROBE to run real media checks.");
        var directory = Path.Combine(Path.GetTempPath(), "xtream-media-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var options = new ServerOptions { DataDirectory = directory, MinimumFreeBytes = 16L * 1024 * 1024, Ffmpeg = ffmpeg!, Ffprobe = ffprobe! };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = timeout.Token;
        try
        {
            var source = Path.Combine(directory, "fixture.mkv");
            var subtitles = Path.Combine(directory, "fixture.srt");
            await File.WriteAllTextAsync(subtitles, "1\n00:00:00,000 --> 00:00:05,000\nHello offline\n", ct);
            await MediaEngine.Run(ffmpeg!, ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=320x180:rate=24",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-i", subtitles, "-t", "6",
                "-map", "0:v", "-map", "1:a", "-map", "2:s", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-g", "48", "-c:a", "aac", "-c:s", "srt", source], ct);
            await MediaEngine.Run(ffmpeg!, ["-nostdin", "-v", "error", "-y", "-i", source, "-map", "0:v", "-map", "0:a",
                "-c", "copy", "-sn", "-f", "hls", "-hls_time", "2", "-hls_list_size", "0", Path.Combine(directory, "upstream.m3u8")], ct);
            var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory, "keys")));
            var store = new ServerStore(options, protection);
            var device = "fixture-device";
            store.Put("device", device, new ServerStore.DeviceRecord(device, "Media test", DateTimeOffset.UtcNow));
            store.Put("profile", "provider", new Profile { Id = "provider", Name = "Fixture", ServerUrl = "https://fixture.example",
                Username = "user", Password = "password", PreferredLiveFormat = LiveStreamFormat.Hls });
            var factory = new MediaHttpFactory(source);
            var catalogs = new CatalogService(store, factory);
            var tickets = new MediaTickets(protection, store, options);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            await using var app = builder.Build();
            app.MapGet("/media/relay/{name}", (string t, Microsoft.AspNetCore.Http.HttpContext c) => Relay.Serve(c, t, tickets, factory));
            await app.StartAsync(ct);
            options.InternalUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            options.PublicUrl = options.InternalUrl;
            await using var engine = new MediaEngine(options, catalogs, tickets);
            var plain = await engine.Open(new("provider", "vod", "7"), device, ct);
            var session = engine.Session(plain.Id, device);
            Assert.True(File.Exists(Path.Combine(session.Directory, "index.m3u8")));
            Assert.Contains(plain.Tracks, t => t.Type == "subtitle");
            engine.Close(plain.Id);
            var live = await engine.Open(new("provider", "live", "5"), device, ct);
            Assert.True(File.Exists(Path.Combine(engine.Session(live.Id, device).Directory, "index.m3u8")));
            engine.Close(live.Id);
            var captioned = await engine.Open(new("provider", "vod", "7", SubtitleTrack: 2), device, ct);
            var captionSession = engine.Session(captioned.Id, device);
            await captionSession.Process.WaitForExitAsync(ct);
            Assert.Equal(0, captionSession.Process.ExitCode);
            var master = await File.ReadAllTextAsync(Path.Combine(captionSession.Directory, "master.m3u8"), ct);
            Assert.Contains("TYPE=SUBTITLES", master);
            Assert.NotEmpty(Directory.GetFiles(captionSession.Directory, "*.vtt"));
            engine.Close(captioned.Id);
            using var jobs = new JobService(store, options, catalogs, engine, tickets, factory);
            await jobs.StartAsync(ct);
            var job = await jobs.Create(new(new("provider", "vod", "7", SubtitleTrack: 2)), device);
            MediaJob current;
            do { await Task.Delay(100, ct); current = jobs.List().Single(j => j.Id == job.Id); }
            while (current.State is "Queued" or "Downloading" or "Preparing");
            Assert.True(current.State == "Completed", current.State + ": " + current.Error);
            Assert.True(new FileInfo(jobs.FileFor(job.Id)).Length > 0);
            var recording = await jobs.Create(new(new("provider", "live", "5"), Recording: true, RecordingMinutes: 1), device);
            await Completed(jobs, recording.Id, ct);
            await jobs.StopAsync(ct);

            // A process restart resumes validated byte downloads and marks recordings interrupted.
            var resumed = await jobs.Create(new(new("provider", "vod", "7")), device);
            var sourceBytes = await File.ReadAllBytesAsync(source, ct);
            var offset = sourceBytes.Length / 2;
            Directory.CreateDirectory(jobs.DirectoryFor(resumed.Id));
            await File.WriteAllBytesAsync(Path.Combine(jobs.DirectoryFor(resumed.Id), "source.bin"), sourceBytes[..offset], ct);
            store.Put("validator", resumed.Id, "\"fixture-v1\"");
            store.Put("job", resumed.Id, resumed with { State = "Downloading" });
            var interrupted = await jobs.Create(new(new("provider", "live", "5"), Recording: true, RecordingMinutes: 1), device);
            Directory.CreateDirectory(jobs.DirectoryFor(interrupted.Id));
            File.Copy(source, Path.Combine(jobs.DirectoryFor(interrupted.Id), "source.ts"));
            store.Put("job", interrupted.Id, interrupted with { State = "Recording" });
            using var restarted = new JobService(store, options, catalogs, engine, tickets, factory);
            await restarted.StartAsync(ct);
            await Completed(restarted, resumed.Id, ct);
            Assert.Contains((long)offset, factory.DownloadOffsets);
            Assert.Equal("Interrupted", restarted.List().Single(j => j.Id == interrupted.Id).State);
            restarted.Recover(interrupted.Id);
            await Completed(restarted, interrupted.Id, ct);
            await restarted.StopAsync(ct);
            options.MaxMediaBytes = 1;
            Assert.Throws<InvalidOperationException>(restarted.EnsureSpace);

            using var lease1 = await engine.Slot("provider", ct);
            using var lease2 = await engine.Slot("provider", ct);
            await Assert.ThrowsAsync<ServerBusyException>(() => engine.Slot("provider", ct));
            await app.StopAsync(ct);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }
    private static async Task Completed(JobService jobs, string id, CancellationToken ct)
    {
        MediaJob job;
        do { await Task.Delay(100, ct); job = jobs.List().Single(j => j.Id == id); }
        while (job.State is "Queued" or "Downloading" or "Preparing" or "Recording");
        Assert.True(job.State == "Completed", job.State + ": " + job.Error);
        Assert.True(new FileInfo(jobs.FileFor(id)).Length > 0);
    }
    private sealed class MediaHttpFactory(string path) : IHttpClientFactory
    {
        public System.Collections.Concurrent.ConcurrentQueue<long> DownloadOffsets { get; } = new();
        public HttpClient CreateClient(string name) => new(new Handler(path, DownloadOffsets)) { Timeout = TimeSpan.FromMinutes(1) };
    }
    private sealed class Handler(string path, System.Collections.Concurrent.ConcurrentQueue<long> offsets) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.Contains("player_api.php"))
            {
                var body = request.RequestUri.Query.Contains("get_live_categories") ? """[{"category_id":"1","category_name":"Fixture Live"}]""" :
                    request.RequestUri.Query.Contains("get_live_streams") ? """[{"stream_id":5,"name":"Fixture Live","category_id":"1"}]""" :
                    request.RequestUri.Query.Contains("get_vod_categories") ? """[{"category_id":"1","category_name":"Fixture"}]""" :
                    request.RequestUri.Query.Contains("get_vod_streams") ? """[{"stream_id":7,"name":"Fixture","category_id":"1","container_extension":"mkv"}]""" :
                    """{"user_info":{"status":"Active","max_connections":"2","allowed_output_formats":["m3u8"]},"server_info":{}}""";
                return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json"), RequestMessage = request };
            }
            var file = request.RequestUri.AbsolutePath.Contains("/live/")
                ? Path.Combine(Path.GetDirectoryName(path)!, request.RequestUri.AbsolutePath.EndsWith(".m3u8") ? "upstream.m3u8" : Path.GetFileName(request.RequestUri.AbsolutePath))
                : path;
            var bytes = await File.ReadAllBytesAsync(file, ct);
            var start = request.Headers.Range?.Ranges.First().From ?? 0;
            if (request.Headers.IfRange is not null) offsets.Enqueue(start);
            var end = Math.Min(request.Headers.Range?.Ranges.First().To ?? bytes.Length - 1, bytes.Length - 1);
            var response = new HttpResponseMessage(request.Headers.Range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            { Content = new ByteArrayContent(bytes[(int)start..((int)end + 1)]), RequestMessage = request };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("video/x-matroska");
            if (request.Headers.Range is not null) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, bytes.Length);
            response.Headers.ETag = new EntityTagHeaderValue("\"fixture-v1\"");
            return response;
        }
    }
}
