using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using XtreamPlayer.Application;

namespace XtreamPlayer.Server;

public sealed class MediaEngine(ServerOptions options, CatalogService catalogs, MediaTickets tickets) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, RunningSession> _sessions = new();
    private readonly ConcurrentDictionary<string, int> _connections = new();
    private readonly SemaphoreSlim _transcodes = new(options.MaxTranscodes);
    private readonly object _slotGate = new();
    public async Task<IDisposable> Slot(string provider, CancellationToken ct)
    {
        var account = await catalogs.Client(provider).AuthenticateAsync(ct: ct);
        lock (_slotGate)
        {
            var used = _connections.GetValueOrDefault(provider);
            if (used >= (account.MaxConnections ?? 1)) throw new ServerBusyException("Provider connection limit reached. Stop playback or pause a job first.");
            _connections[provider] = used + 1;
        }
        return new Lease(() => { lock (_slotGate) _connections[provider]--; });
    }
    public async Task<IDisposable> Transcode(CancellationToken ct)
    {
        if (!await _transcodes.WaitAsync(0, ct)) throw new ServerBusyException("The server is busy converting another video.");
        return new Lease(() => _transcodes.Release());
    }
    public static Process Start(string executable, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start media engine.");
        // Never log upstream URLs, ffmpeg arguments or stderr (they may include credentials).
        return process;
    }
    public static async Task Run(string executable, IEnumerable<string> args, CancellationToken ct)
    {
        using var process = Start(executable, args);
        var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, ct);
        var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, ct);
        try { await process.WaitForExitAsync(ct); await Task.WhenAll(error, output); }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); } }
        if (process.ExitCode != 0) throw new InvalidOperationException("Media conversion failed. The stream may be unavailable or unsupported.");
    }
    public async Task<(List<MediaTrack> Tracks, double Duration, bool IsHls)> Probe(string input, CancellationToken ct, bool local = false)
    {
        using var process = Start(options.Ffprobe, ["-v", "error", "-protocol_whitelist", local ? "file" : "http,tcp,crypto", "-format_whitelist", local ? "mov,matroska,webm,mpegts,mpegvideo,aac,mp3,avi,flv,ogg" : "mov,matroska,webm,mpegts,hls,mpegvideo,aac,mp3,avi,flv,ogg,webvtt", "-allowed_extensions", "ALL", "-analyzeduration", "5000000",
            "-probesize", "5000000", "-show_streams", "-show_format", "-of", "json", input]);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); await errors; }
        finally { if (!process.HasExited) process.Kill(true); }
        if (process.ExitCode != 0) throw new InvalidOperationException("Could not inspect this stream.");
        using var doc = JsonDocument.Parse(await output);
        var tracks = new List<MediaTrack>();
        foreach (var s in doc.RootElement.GetProperty("streams").EnumerateArray())
        {
            var tags = s.TryGetProperty("tags", out var t) ? t : default;
            string Tag(string name) => tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
            tracks.Add(new(s.GetProperty("index").GetInt32(), s.GetProperty("codec_type").GetString() ?? "",
                Tag("language"), Tag("title"), s.TryGetProperty("codec_name", out var codec) ? codec.GetString() ?? "" : ""));
        }
        double duration = 0;
        if (doc.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var d))
            double.TryParse(d.GetString(), CultureInfo.InvariantCulture, out duration);
        var isHls = doc.RootElement.TryGetProperty("format", out var inputFormat) &&
            inputFormat.TryGetProperty("format_name", out var name) && name.GetString() == "hls";
        return (tracks, duration, isHls);
    }
    public async Task<PlaybackSession> Open(PlayRequest request, string device, CancellationToken ct)
    {
        var source = await catalogs.Resolve(request);
        var slot = await Slot(request.ProviderId, ct);
        IDisposable? conversion = null;
        Process? process = null;
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(options.MediaDirectory, "sessions", id);
        try
        {
            var input = tickets.Relay(device, source.Url);
            var probe = await Probe(input, ct);
            var audio = request.AudioTrack ?? probe.Tracks.FirstOrDefault(t => t.Type == "audio")?.Index;
            if (audio is not null && !probe.Tracks.Any(t => t.Type == "audio" && t.Index == audio)) throw new ArgumentException("Unknown audio track.");
            if (request.SubtitleTrack is not null && !probe.Tracks.Any(t => t.Type == "subtitle" && t.Index == request.SubtitleTrack)) throw new ArgumentException("Unknown subtitle track.");
            var selectedSubtitle = probe.Tracks.FirstOrDefault(t => t.Index == request.SubtitleTrack);
            var bitmapSubtitle = selectedSubtitle?.Codec is "dvd_subtitle" or "dvb_subtitle" or "hdmv_pgs_subtitle";
            var textSubtitle = selectedSubtitle is not null && !bitmapSubtitle;
            var copyVideo = probe.Tracks.FirstOrDefault(t => t.Type == "video")?.Codec == "h264" && !bitmapSubtitle;
            if (!copyVideo) conversion = await Transcode(ct);
            Directory.CreateDirectory(directory);
            var args = BuildHlsArguments(input, directory, request, audio, copyVideo, bitmapSubtitle, textSubtitle, probe.IsHls);
            var playlist = textSubtitle ? "master.m3u8" : "index.m3u8";
            process = Start(options.Ffmpeg, args);
            var running = new RunningSession(id, device, request.ProviderId, directory, process, slot, conversion);
            _sessions[id] = running;
            _ = Drain(process.StandardError.BaseStream); _ = Drain(process.StandardOutput.BaseStream);
            for (var i = 0; i < 120 && !File.Exists(Path.Combine(directory, playlist)); i++)
            {
                if (process.HasExited) throw new InvalidOperationException("This stream could not be played.");
                await Task.Delay(500, ct);
            }
            if (!File.Exists(Path.Combine(directory, playlist))) throw new InvalidOperationException("The stream did not become ready.");
            var token = Uri.EscapeDataString(tickets.Issue(device, "session", id));
            return new(id, tickets.Public($"/media/sessions/{id}/{playlist}?t={token}"), request.PositionSeconds,
                request.Kind == "live" && request.Start is null ? 0 : probe.Duration, probe.Tracks,
                new(request.Kind != "live" && probe.Duration > 0 || request.Start is not null, true, true, true, false));
        }
        catch
        {
            if (_sessions.ContainsKey(id)) Close(id);
            else { if (process is not null && !process.HasExited) process.Kill(true); process?.Dispose(); slot.Dispose(); conversion?.Dispose(); }
            throw;
        }
    }
    public static List<string> BuildHlsArguments(string input, string directory, PlayRequest request,
        int? audio, bool copyVideo, bool bitmapSubtitle, bool textSubtitle, bool hlsInput = false)
    {
        var args = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
            "-protocol_whitelist", "http,tcp,crypto", "-format_whitelist", "mov,matroska,webm,mpegts,hls,mpegvideo,aac,mp3,avi,flv,ogg,webvtt" };
        if (hlsInput) args.AddRange(["-allowed_extensions", "ALL"]);
        if (request.PositionSeconds > 0) args.AddRange(["-ss", request.PositionSeconds.ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(["-i", input]);
        if (bitmapSubtitle)
            args.AddRange(["-filter_complex", $"[0:v:0][0:{request.SubtitleTrack}]overlay[v]", "-map", "[v]"]);
        else args.AddRange(["-map", "0:v:0?"]);
        if (audio is not null) args.AddRange(["-map", $"0:{audio}"]);
        if (textSubtitle) args.AddRange(["-map", $"0:{request.SubtitleTrack}", "-c:s", "webvtt"]);
        else args.Add("-sn");
        args.AddRange(["-c:v", copyVideo ? "copy" : "libx264"]);
        if (!copyVideo) args.AddRange(["-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p"]);
        args.AddRange(["-c:a", "aac", "-ac", "2", "-b:a", "128k", "-f", "hls", "-hls_time", "4",
            "-hls_list_size", request.Kind == "live" && request.Start is null ? "8" : "0",
            "-hls_flags", request.Kind == "live" && request.Start is null ? "delete_segments+independent_segments" : "independent_segments"]);
        if (textSubtitle)
            args.AddRange(["-var_stream_map", "v:0" + (audio is not null ? ",a:0" : "") + ",s:0,sgroup:subs,sname:Subtitles",
                "-master_pl_name", "master.m3u8"]);
        args.AddRange(["-hls_segment_filename", Path.Combine(directory, "segment-%06d.ts"), Path.Combine(directory, "index.m3u8")]);
        return args;
    }
    public RunningSession Session(string id, string device)
    {
        if (!_sessions.TryGetValue(id, out var session) || session.Device != device) throw new KeyNotFoundException();
        session.LastSeen = DateTimeOffset.UtcNow;
        return session;
    }
    private static async Task Drain(Stream stream)
    {
        try { await stream.CopyToAsync(Stream.Null); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
    public void Close(string id)
    {
        if (!_sessions.TryRemove(id, out var s)) return;
        try { if (!s.Process.HasExited) { s.Process.Kill(true); s.Process.WaitForExit(5000); } } catch (InvalidOperationException) { }
        s.Process.Dispose(); s.Slot.Dispose(); s.Conversion?.Dispose();
        try { Directory.Delete(s.Directory, true); } catch (IOException) { }
    }
    public void StopProvider(string provider) { foreach (var s in _sessions.Values.Where(s => s.Provider == provider)) Close(s.Id); }
    public void StopAll() { foreach (var id in _sessions.Keys) Close(id); }
    public void Reap()
    {
        foreach (var s in _sessions.Values)
        {
            if (s.LastSeen < DateTimeOffset.UtcNow.AddMinutes(-2)) { Close(s.Id); continue; }
            try { if (s.Process.HasExited) { s.Slot.Dispose(); s.Conversion?.Dispose(); } }
            catch (InvalidOperationException) { /* A concurrent close already disposed the process. */ }
        }
    }
    public ValueTask DisposeAsync() { foreach (var id in _sessions.Keys) Close(id); _transcodes.Dispose(); return ValueTask.CompletedTask; }
    public sealed class RunningSession(string id, string device, string provider, string directory, Process process, IDisposable slot, IDisposable? conversion)
    {
        public string Id { get; } = id;
        public string Device { get; } = device;
        public string Provider { get; } = provider;
        public string Directory { get; } = directory;
        public Process Process { get; } = process;
        public IDisposable Slot { get; } = slot;
        public IDisposable? Conversion { get; } = conversion;
        public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
    }
    private sealed class Lease(Action release) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) release(); }
    }
}


public sealed class ServerBusyException(string message) : InvalidOperationException(message);
