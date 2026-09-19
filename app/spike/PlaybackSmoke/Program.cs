using System.Diagnostics;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Playback;

namespace PlaybackSmoke;

/// <summary>
/// Headless Phase 0 harness: exercises everything on the de-risk checklist that
/// does not require human eyes, against a real stream.
/// </summary>
/// <remarks>
/// <para>
/// Proves demux, decode, track enumeration, track switching, seeking and clean
/// teardown — but NOT that pixels reach an embedded Avalonia surface. That last
/// question is what the VideoSpike GUI is for, and it needs a person looking at
/// the window. libvlc opens its own throwaway window while this runs.
/// </para>
/// <para>Usage: <c>PlaybackSmoke [url] [live|vod]</c></para>
/// </remarks>
internal static class Program
{
    // Apple's advanced BipBop sample: multiple audio renditions and WebVTT
    // subtitles, which is exactly what the track-switching checks need.
    private const string DefaultUrl =
        "https://devstreaming-cdn.apple.com/videos/streaming/examples/img_bipbop_adv_example_fmp4/master.m3u8";

    private static int _failures;

    public static async Task<int> Main(string[] args)
    {
        // Unbuffered: a native crash must not swallow the line that says where
        // we were when it happened.
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });

        var url = args.Length > 0 ? args[0] : DefaultUrl;
        var kind = args.Length > 1 && args[1].Equals("live", StringComparison.OrdinalIgnoreCase)
            ? ContentKind.Live
            : ContentKind.Vod;

        Console.WriteLine($"Stream : {url}");
        Console.WriteLine($"Kind   : {kind}");
        Console.WriteLine(new string('-', 72));

        try
        {
            VlcLocator.Initialize();
            Report("libvlc initializes", true, VlcLocator.ResolvedDirectory);
        }
        catch (VlcInitializationException ex)
        {
            Report("libvlc initializes", false, ex.Message);
            return 2;
        }

        // Deliberately NOT --vout=dummy. libvlc 3.0.23 segfaults roughly a
        // second after a seek when the dummy video output is active; verified
        // against bare LibVLCSharp with no wrapper, so it is a library bug and
        // not ours. With a real vout the same seek is clean. libvlc opens its
        // own window here, which is fine for a harness and never happens in the
        // app, where VideoView owns the surface.
        using var engine = new LibVlcEngine(new LibVlcOptions
        {
            // SMOKE_DIAGNOSTICS=0 to measure without --verbose=2, which routes
            // through libvlc's console logger and is very slow on a redirected
            // pipe.
            Diagnostics = Environment.GetEnvironmentVariable("SMOKE_DIAGNOSTICS") != "0",
        });

        var playback = new VlcPlaybackService(engine);
        await using (playback.ConfigureAwait(false))
        {
            var playing = WaitForStateAsync(playback, PlaybackState.Playing, TimeSpan.FromSeconds(40));

            await playback.OpenAsync(new PlaybackRequest(url, "Smoke test", kind))
                .ConfigureAwait(false);

            var reachedPlaying = await playing.ConfigureAwait(false);
            Report("reaches Playing", reachedPlaying, $"state={playback.State}");

            if (!reachedPlaying)
            {
                Console.WriteLine();
                Console.WriteLine("libvlc log tail:");
                Console.WriteLine(engine.LogTail());
                return Finish();
            }

            // Tracks can arrive late; the service re-scans 1.5 s after Playing.
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);

            DumpTracks(playback);

            Report("audio tracks enumerate", playback.AudioTracks.Count > 0,
                $"{playback.AudioTracks.Count} track(s)");

            await CheckAudioSwitchingAsync(playback).ConfigureAwait(false);
            await CheckSubtitleSwitchingAsync(playback).ConfigureAwait(false);
            CheckSeekability(playback, kind);

            Console.WriteLine("  ... seeking");
            await CheckSeekAsync(playback, kind).ConfigureAwait(false);

            Console.WriteLine("  ... stopping");
            await CheckStopIsNotBlockingAsync(playback).ConfigureAwait(false);

            Console.WriteLine("  ... disposing service");
        }

        Report("disposes without hanging", true);
        return Finish();
    }

    // ---- Checks ---------------------------------------------------------

    private static void DumpTracks(IPlaybackService playback)
    {
        Console.WriteLine();
        Console.WriteLine($"  Audio tracks (selected id {playback.SelectedAudioTrackId}):");
        foreach (var track in playback.AudioTracks)
        {
            Console.WriteLine($"    [{track.Id,3}] {track.Label}  lang={track.Language ?? "-"}");
        }

        Console.WriteLine($"  Subtitle tracks (selected id {playback.SelectedSubtitleTrackId}):");
        foreach (var track in playback.SubtitleTracks)
        {
            Console.WriteLine($"    [{track.Id,3}] {track.Label}  lang={track.Language ?? "-"}");
        }

        Console.WriteLine();
    }

    private static async Task CheckAudioSwitchingAsync(IPlaybackService playback)
    {
        if (playback.AudioTracks.Count < 2)
        {
            Report("audio switching", true, "skipped — stream has fewer than 2 audio tracks");
            return;
        }

        var before = playback.SelectedAudioTrackId;
        var target = playback.AudioTracks.First(t => t.Id != before);
        var targetLabel = target.Label;

        playback.SetAudioTrack(target.Id);

        // Long enough for the service's post-switch re-scan (1.5 s) to land.
        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);

        var after = playback.SelectedAudioTrackId;

        // Asserting id equality here would be wrong. On HLS, switching to an
        // alternate audio rendition makes libvlc build a NEW elementary stream
        // with a fresh id, so the selected id legitimately differs from the one
        // requested. What must hold is that the selection actually moved and
        // now resolves to the track we asked for by name.
        var moved = after != before;
        var resolved = playback.AudioTracks.FirstOrDefault(t => t.Id == after);
        var correctTrack = resolved is not null && resolved.Label == targetLabel;

        Report("audio switching mid-stream", moved && (correctTrack || after == target.Id),
            $"{before} -> {after} (asked for {target.Id} \"{targetLabel}\"; "
          + $"now resolves to \"{resolved?.Label ?? "<not in list>"}\")");
    }

    private static async Task CheckSubtitleSwitchingAsync(IPlaybackService playback)
    {
        var real = playback.SubtitleTracks.Where(t => t.Id >= 0).ToList();
        if (real.Count == 0)
        {
            Report("subtitle switching", true, "skipped — stream has no subtitle tracks");
            return;
        }

        playback.SetSubtitleTrack(real[0].Id);
        await Task.Delay(TimeSpan.FromMilliseconds(1200)).ConfigureAwait(false);
        var enabled = playback.SelectedSubtitleTrackId == real[0].Id;

        playback.SetSubtitleTrack(-1);
        await Task.Delay(TimeSpan.FromMilliseconds(800)).ConfigureAwait(false);
        var disabled = playback.SelectedSubtitleTrackId == -1;

        Report("subtitle on then off", enabled && disabled,
            $"on={enabled} off={disabled}");
    }

    private static void CheckSeekability(IPlaybackService playback, ContentKind kind)
    {
        if (kind == ContentKind.Live)
        {
            // The rule that keeps a dead seek bar off the live player.
            Report("live reports not seekable", !playback.IsSeekable);
        }
        else
        {
            Report("VOD reports seekable", playback.IsSeekable,
                $"duration={playback.Duration}");
        }
    }

    private static async Task CheckSeekAsync(IPlaybackService playback, ContentKind kind)
    {
        if (kind == ContentKind.Live || !playback.IsSeekable)
        {
            Report("seek", true, "skipped — not seekable");
            return;
        }

        var target = TimeSpan.FromSeconds(30);
        await playback.SeekAsync(target).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);

        var delta = (playback.Position - target).Duration();
        Report("seek lands within 5s of target", delta < TimeSpan.FromSeconds(5),
            $"asked {target}, got {playback.Position}");
    }

    private static async Task CheckStopIsNotBlockingAsync(IPlaybackService playback)
    {
        // Stop() blocks until the decoder thread joins. The service runs it on
        // the thread pool; this measures that the caller is not the one waiting.
        var stopwatch = Stopwatch.StartNew();
        var stopTask = playback.StopAsync();
        var returnedImmediately = !stopTask.IsCompleted || stopwatch.ElapsedMilliseconds < 50;

        await stopTask.ConfigureAwait(false);
        stopwatch.Stop();

        Report("StopAsync does not block the caller", returnedImmediately,
            $"total stop took {stopwatch.ElapsedMilliseconds} ms");
    }

    // ---- Plumbing -------------------------------------------------------

    private static Task<bool> WaitForStateAsync(
        IPlaybackService playback,
        PlaybackState target,
        TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnChanged(object? sender, PlaybackStateChangedEventArgs e)
        {
            if (e.Current == target)
            {
                playback.StateChanged -= OnChanged;
                tcs.TrySetResult(true);
            }
            else if (e.Current == PlaybackState.Error)
            {
                playback.StateChanged -= OnChanged;
                tcs.TrySetResult(false);
            }
        }

        playback.StateChanged += OnChanged;

        _ = Task.Delay(timeout).ContinueWith(_ =>
        {
            playback.StateChanged -= OnChanged;
            tcs.TrySetResult(false);
        }, TaskScheduler.Default);

        return tcs.Task;
    }

    private static void Report(string what, bool ok, string? detail = null)
    {
        if (!ok)
        {
            _failures++;
        }

        var mark = ok ? "PASS" : "FAIL";
        Console.WriteLine(detail is null
            ? $"  [{mark}] {what}"
            : $"  [{mark}] {what}  ({detail})");
    }

    private static int Finish()
    {
        Console.WriteLine(new string('-', 72));
        Console.WriteLine(_failures == 0
            ? "All headless checks passed."
            : $"{_failures} check(s) FAILED.");
        Console.WriteLine();
        Console.WriteLine("Still requires human eyes in VideoSpike: video renders in the");
        Console.WriteLine("embedded surface, and the overlay probe is hidden behind it.");

        return _failures == 0 ? 0 : 1;
    }
}
