using System.Collections.Concurrent;
using LibVLCSharp.Shared;

namespace XtreamPlayer.Playback;

public sealed class LibVlcOptions
{
    /// <summary>
    /// Must match the catalog HttpClient's user agent.
    /// libvlc fetches streams over its OWN HTTP stack, so a provider that gates
    /// on UA will serve catalogs fine and 403 every stream — a failure that
    /// looks like a codec problem and is not.
    /// </summary>
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) XtreamPlayer/1.0";

    /// <summary>Set false to diagnose green/garbled video.</summary>
    public bool HardwareDecoding { get; set; } = true;

    /// <summary>
    /// Turns on --verbose=2 and the in-memory log ring. The only way to
    /// diagnose provider-side stream failures; retrofitting it mid-incident is
    /// miserable, so it exists from Phase 0.
    /// </summary>
    public bool Diagnostics { get; set; }

    /// <summary>The vlc.path setting. Null uses bundled/system natives.</summary>
    public string? VlcDirectoryOverride { get; set; }

    /// <summary>
    /// Appended verbatim to the libvlc argument list. Used by the headless
    /// smoke harness (--vout=dummy) and available for field diagnostics.
    /// </summary>
    public IReadOnlyList<string> ExtraArguments { get; set; } = [];
}

/// <summary>
/// Owns the single process-wide <see cref="LibVLC"/> instance.
/// </summary>
public sealed class LibVlcEngine : IDisposable
{
    private const int LogRingCapacity = 400;

    private readonly ConcurrentQueue<string> _logRing = new();
    private bool _disposed;

    public LibVlcEngine(LibVlcOptions? options = null)
    {
        Options = options ?? new LibVlcOptions();

        VlcLocator.Initialize(Options.VlcDirectoryOverride);

        LibVlc = new LibVLC(BuildArguments(Options));

        if (Options.Diagnostics)
        {
            LibVlc.Log += OnLog;
        }
    }

    public LibVlcOptions Options { get; }

    public LibVLC LibVlc { get; }

    /// <summary>
    /// The tail of the libvlc log, for the player's error panel. Empty unless
    /// <see cref="LibVlcOptions.Diagnostics"/> is on.
    /// </summary>
    public string LogTail(int lines = 40) =>
        string.Join(Environment.NewLine, _logRing.TakeLast(lines));

    private static string[] BuildArguments(LibVlcOptions options)
    {
        var args = new List<string>
        {
            "--no-video-title-show",
            "--no-osd",
            "--no-snapshot-preview",
            "--http-reconnect",
            $"--http-user-agent={options.UserAgent}",
            options.HardwareDecoding ? "--avcodec-hw=any" : "--avcodec-hw=none",
        };

        // --quiet suppresses the Log event entirely, so the two are exclusive.
        args.Add(options.Diagnostics ? "--verbose=2" : "--quiet");

        args.AddRange(options.ExtraArguments);

        return [.. args];
    }

    private void OnLog(object? sender, LogEventArgs e)
    {
        // Runs on a libvlc thread. Do nothing here but append to the ring —
        // never touch a MediaPlayer from a libvlc callback.
        _logRing.Enqueue($"[{e.Level}] {e.Module}: {e.Message}");

        while (_logRing.Count > LogRingCapacity && _logRing.TryDequeue(out _))
        {
            // Trim to capacity.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (Options.Diagnostics)
        {
            LibVlc.Log -= OnLog;
        }

        LibVlc.Dispose();
    }
}
