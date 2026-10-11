using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using XtreamPlayer.Application;

namespace XtreamPlayer.Server;

public sealed class JobService(ServerStore store, ServerOptions options, CatalogService catalogs,
    MediaEngine engine, MediaTickets tickets, IHttpClientFactory clients) : BackgroundService
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();
    private readonly object _gate = new();
    public List<MediaJob> List() => store.List<MediaJob>("job").OrderByDescending(j => j.CreatedAt).ToList();
    public string DirectoryFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid media ID.");
        return Path.Combine(options.MediaDirectory, "jobs", id);
    }
    public string FileFor(string id) => Path.Combine(DirectoryFor(id), "offline.mp4");
    public void EnsureSpace()
    {
        long used = 0;
        // Sessions and completed jobs may be removed while maintenance counts their files.
        foreach (var path in Directory.EnumerateFiles(options.MediaDirectory, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            try { used += new FileInfo(path).Length; } catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
        var mediaPath = Path.GetFullPath(options.MediaDirectory);
        var drive = DriveInfo.GetDrives().Where(d => d.IsReady && mediaPath.StartsWith(d.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.RootDirectory.FullName.Length).First();
        if (used >= options.MaxMediaBytes || drive.AvailableFreeSpace < options.MinimumFreeBytes)
            throw new InvalidOperationException("Server media storage is full. Delete a completed item before continuing.");
    }
    public async Task<MediaJob> Create(CreateJob request, string device)
    {
        if (request.Recording && (request.Media.Kind != "live" || request.Media.Start is not null ||
            request.RecordingMinutes is < 1 or > 1440)) throw new ArgumentException("Choose a live channel and a duration between 1 and 1440 minutes.");
        if (!request.Recording && request.Media.Kind == "live" && request.Media.Start is null)
            throw new ArgumentException("Use Record for a live stream.");
        EnsureSpace();
        var source = await catalogs.Resolve(request.Media);
        var job = new MediaJob(Guid.NewGuid().ToString("N"), request.Media.ProviderId, source.Title, "Queued",
            request.Recording, 0, 0, null, DateTimeOffset.UtcNow, request.Media, request.RecordingMinutes);
        lock (_gate) { store.Put("job", job.Id, job); store.Put("job-device", job.Id, device); }
        return job;
    }
    public void Change(string id, string action)
    {
        lock (_gate)
        {
            var job = store.Get<MediaJob>("job", id) ?? throw new KeyNotFoundException();
            var state = action switch
            {
                "pause" when !job.Recording && job.State is "Queued" or "Downloading" or "Preparing" => "Paused",
                "resume" when job.State is "Paused" or "Failed" or "Cancelled" && !job.Recording => "Queued",
                "stop" when job.Recording && job.State == "Recording" => "Finishing",
                "cancel" when job.State != "Completed" => "Cancelled",
                "delete" => "Deleting",
                _ => throw new ArgumentException("This action is not available for the current job.")
            };
            store.Put("job", id, job with { State = state, Error = null });
            if (_active.TryGetValue(id, out var cancellation)) cancellation.Cancel();
            else if (action == "delete") Delete(job);
        }
    }
    private void Delete(MediaJob job)
    {
        var directory = DirectoryFor(job.Id);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        store.Delete("job", job.Id); store.Delete("job-device", job.Id); store.Delete("source-complete", job.Id); store.Delete("recover", job.Id); store.Delete("validator", job.Id);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var job in List())
        {
            if (job.State is "Recording" or "Finishing")
                store.Put("job", job.Id, job with { State = "Interrupted", Error = "Server restarted during recording. Recover the partial recording." });
            else if (job.State is "Downloading" or "Preparing")
                store.Put("job", job.Id, job with { State = "Queued" });
            else if (job.State == "Deleting") Delete(job);
        }
        // A single queue worker bounds disk and provider load. Playback has its own connection lease.
        while (!stoppingToken.IsCancellationRequested)
        {
            engine.Reap();
            var job = List().FirstOrDefault(j => j.State == "Queued");
            if (job is not null) await Process(job, stoppingToken);
            try { await Task.Delay(1000, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
    public void StorageFull()
    {
        lock (_gate)
        {
            foreach (var id in _active.Keys)
            {
                var job = store.Get<MediaJob>("job", id);
                if (job is not null) store.Put("job", id, job with { State = "Failed", Error = "Server media storage is full." });
                if (_active.TryGetValue(id, out var cancel)) cancel.Cancel();
            }
        }
    }
    public void Recover(string id)
    {
        lock (_gate)
        {
            var job = store.Get<MediaJob>("job", id) ?? throw new KeyNotFoundException();
            if (job.State != "Interrupted") throw new ArgumentException("Only interrupted recordings can be recovered.");
            store.Put("job", id, job with { State = "Queued", Error = null });
            store.Put("recover", id, true);
        }
    }
    private void Update(string id, Func<MediaJob, MediaJob> update)
    {
        lock (_gate)
        {
            var current = store.Get<MediaJob>("job", id);
            if (current is not null && current.State is not ("Deleting" or "Cancelled" or "Paused" or "Finishing"))
                store.Put("job", id, update(current));
        }
    }

    private async Task DownloadSource(MediaJob job, string source, string device, string raw, CancellationToken ct)
    {
        var offset = File.Exists(raw) ? new FileInfo(raw).Length : 0;
        var validator = store.Get<string>("validator", job.Id);
        // Without a validator the upstream may have replaced the file while paused. Restart safely.
        if (validator is null) offset = 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
            request.Headers.TryAddWithoutValidation("If-Range", validator);
        }
        using var response = await clients.CreateClient("upstream").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var currentValidator = response.Headers.ETag?.ToString() ?? response.Content.Headers.LastModified?.ToString("R");
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable &&
            response.Content.Headers.ContentRange?.Length == offset && offset > 0 && currentValidator == validator) return;
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.PartialContent &&
            (response.Content.Headers.ContentRange?.From != offset || (currentValidator is not null && currentValidator != validator)))
            throw new InvalidOperationException("Provider returned an invalid or changed download range.");
        if (response.StatusCode != HttpStatusCode.PartialContent) offset = 0;
        var isHls = response.RequestMessage?.RequestUri?.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) == true ||
            response.Content.Headers.ContentType?.MediaType?.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) == true;
        if (isHls)
        {
            response.Dispose();
            await MediaEngine.Run(options.Ffmpeg, ["-nostdin", "-v", "error", "-y", "-protocol_whitelist", "http,tcp,crypto",
                "-format_whitelist", "hls,mpegts,mov,aac,webvtt", "-allowed_extensions", "ALL", "-i", tickets.Relay(device, source),
                "-map", "0:v:0?", "-map", "0:a?", "-map", "0:s?", "-c", "copy", "-f", "matroska", raw], ct);
            return;
        }
        if (currentValidator is not null) store.Put("validator", job.Id, currentValidator);
        else store.Delete("validator", job.Id);
        var total = response.Content.Headers.ContentRange?.Length ?? (response.Content.Headers.ContentLength is { } n ? n + offset : 0);
        if (total > options.MaxMediaBytes) throw new InvalidOperationException("This video exceeds the server storage limit.");
        await using var output = new FileStream(raw, offset == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.Read, 131072, true);
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[131072]; int count; var checkpoint = DateTime.UtcNow;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, count), ct); offset += count;
            if (DateTime.UtcNow - checkpoint > TimeSpan.FromSeconds(2))
            {
                EnsureSpace(); checkpoint = DateTime.UtcNow;
                Update(job.Id, j => j with { Bytes = offset, TotalBytes = total });
            }
        }
        if (total > 0 && offset != total) throw new InvalidOperationException("Download ended before all bytes arrived.");
    }

    private async Task Process(MediaJob job, CancellationToken stopping)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _active[job.Id] = cancel;
        var directory = DirectoryFor(job.Id);
        Directory.CreateDirectory(directory);
        var raw = Path.Combine(directory, job.Recording ? "source.ts" : "source.bin");
        try
        {
            if (store.Get<MediaJob>("job", job.Id)?.State != "Queued") return;
            EnsureSpace();
            var device = store.Get<string>("job-device", job.Id) ?? throw new UnauthorizedAccessException();
            var recover = store.Get<bool>("recover", job.Id) || store.Get<bool>("source-complete", job.Id);
            if (!recover)
            {
                using var slot = await engine.Slot(job.ProviderId, cancel.Token);
                var source = await catalogs.Resolve(job.Media);
                if (job.Recording)
                {
                    Update(job.Id, j => j with { State = "Recording" });
                    try
                    {
                        var input = tickets.Relay(device, source.Url);
                        var captureProbe = await engine.Probe(input, cancel.Token);
                        var capture = new List<string> { "-nostdin", "-v", "error", "-y", "-protocol_whitelist", "http,tcp,crypto",
                            "-format_whitelist", "hls,mpegts,mov,matroska,aac,webvtt" };
                        if (captureProbe.IsHls) capture.AddRange(["-allowed_extensions", "ALL"]);
                        capture.AddRange(["-i", input, "-t", (job.RecordingMinutes * 60).ToString(CultureInfo.InvariantCulture),
                            "-map", "0:v:0?", "-map", "0:a?", "-c", "copy", "-f", "mpegts", raw]);
                        await MediaEngine.Run(options.Ffmpeg, capture, cancel.Token);
                    }
                    catch (OperationCanceledException) when (!stopping.IsCancellationRequested && store.Get<MediaJob>("job", job.Id)?.State == "Finishing")
                    {
                        lock (_gate) store.Put("job", job.Id, store.Get<MediaJob>("job", job.Id)! with { State = "Preparing" });
                    }
                }
                else
                {
                    Update(job.Id, j => j with { State = "Downloading" });
                    await DownloadSource(job, source.Url, device, raw, cancel.Token);
                }
            }
            store.Put("source-complete", job.Id, true);
            var state = store.Get<MediaJob>("job", job.Id)?.State;
            if (state is "Cancelled" or "Paused" or "Deleting") return;
            Update(job.Id, j => j with { State = "Preparing" });
            // Local conversion does not consume a provider slot. Pausing restarts conversion, not the completed source download.
            using var finishing = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            if (state == "Preparing" && cancel.IsCancellationRequested) _active[job.Id] = finishing;
            var token = state == "Preparing" && cancel.IsCancellationRequested ? finishing.Token : cancel.Token;
            using var conversion = await engine.Transcode(token);
            var probe = await engine.Probe(raw, token, local: true);
            if (job.Media.AudioTrack is { } selectedAudio && !probe.Tracks.Any(t => t.Type == "audio" && t.Index == selectedAudio))
                throw new InvalidOperationException("The selected audio track is no longer available.");
            var subtitle = probe.Tracks.FirstOrDefault(t => t.Type == "subtitle" && t.Index == job.Media.SubtitleTrack);
            if (job.Media.SubtitleTrack is not null && subtitle is null) throw new InvalidOperationException("The selected subtitle track is no longer available.");
            var bitmap = subtitle?.Codec is "dvd_subtitle" or "dvb_subtitle" or "hdmv_pgs_subtitle";
            const string scale = "scale=w='min(1280,iw)':h='min(720,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2";
            var args = new List<string> { "-nostdin", "-v", "error", "-y", "-protocol_whitelist", "file",
                "-format_whitelist", "mov,matroska,webm,mpegts,mpegvideo,aac,mp3,avi,flv,ogg", "-i", raw };
            if (bitmap)
                args.AddRange(["-filter_complex", $"[0:v:0][0:{subtitle!.Index}]overlay,{scale}[v]", "-map", "[v]"]);
            else args.AddRange(["-map", "0:v:0?", "-vf", scale]);
            args.AddRange(["-map", job.Media.AudioTrack is { } audio ? $"0:{audio}" : "0:a:0?",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-ac", "2", "-b:a", "128k", "-movflags", "+faststart"]);
            if (subtitle is not null && !bitmap) args.AddRange(["-map", $"0:{subtitle.Index}", "-c:s", "mov_text"]);
            else args.Add("-sn");
            var temp = Path.Combine(directory, "offline.partial.mp4");
            args.Add(temp);
            await MediaEngine.Run(options.Ffmpeg, args, token);
            EnsureSpace();
            File.Move(temp, FileFor(job.Id), true);
            var bytes = new FileInfo(FileFor(job.Id)).Length;
            Update(job.Id, j => j with { State = "Completed", Bytes = bytes, TotalBytes = bytes, Error = null });
            File.Delete(raw); store.Delete("recover", job.Id);
        }
        catch (ServerBusyException e)
        {
            Update(job.Id, j => j with { State = "Queued", Error = e.Message });
        }
        catch (OperationCanceledException)
        {
            if (stopping.IsCancellationRequested && job.Recording)
                Update(job.Id, j => j with { State = "Interrupted", Error = "Recording interrupted. Recover the partial file." });
        }
        catch (Exception)
        {
            Update(job.Id, j => j with { State = job.Recording && File.Exists(raw) ? "Interrupted" : "Failed", Error = "Job could not finish. Check provider availability, connection limits, server storage and media compatibility, then retry." });
        }
        finally
        {
            _active.TryRemove(job.Id, out _);
            var current = store.Get<MediaJob>("job", job.Id);
            if (current?.State == "Deleting") Delete(current);
        }
    }
}
