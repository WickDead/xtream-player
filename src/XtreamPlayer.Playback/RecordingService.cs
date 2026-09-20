using System.Collections.Concurrent;
using LibVLCSharp.Shared;

namespace XtreamPlayer.Playback;

public sealed record RecordingSession
{
    public required string Id { get; init; }
    public required string ChannelId { get; init; }
    public required string ChannelName { get; init; }
    public string? ProgrammeTitle { get; init; }
    public required string TargetPath { get; init; }
    public required DateTimeOffset StartedAt { get; init; }

    public TimeSpan Elapsed => DateTimeOffset.UtcNow - StartedAt;

    public long Bytes
    {
        get
        {
            try
            {
                return File.Exists(TargetPath) ? new FileInfo(TargetPath).Length : 0;
            }
            catch (IOException)
            {
                // Being written to right now; the next poll will get it.
                return 0;
            }
        }
    }
}

public sealed class RecordingChangedEventArgs(RecordingSession session, bool started) : EventArgs
{
    public RecordingSession Session { get; } = session;
    public bool Started { get; } = started;
}

public interface IRecordingService
{
    IReadOnlyList<RecordingSession> Active { get; }

    bool IsRecording(string channelId);

    RecordingSession Start(string channelId, string channelName, string streamUrl,
        string targetPath, string? programmeTitle = null);

    Task StopAsync(string id);
    Task StopAllAsync();

    event EventHandler<RecordingChangedEventArgs>? Changed;
}

/// <summary>
/// Records a live stream to disk, independently of what is being watched.
/// </summary>
/// <remarks>
/// <para>
/// Each recording is its own headless <see cref="MediaPlayer"/> with a
/// <c>:sout</c> chain writing MPEG-TS, and no video or audio output. That makes
/// it completely independent of the display pipeline: you can start, stop,
/// change channel or close the player without touching the recording.
/// </para>
/// <para>
/// <c>libvlc_media_player_record()</c> is a LibVLC 4.0 API and <em>does not
/// exist</em> in the 3.0.23 we ship. It appears throughout the documentation,
/// which costs a day if you believe it. On LibVLC 3 the output chain is fixed
/// when the media is opened, so recording has to be decided up front — hence a
/// second player rather than reconfiguring the one already running.
/// </para>
/// <para>
/// The cost is one more provider connection slot. Xtream accounts commonly
/// allow two, so recording while watching can be the limit; the caller is
/// expected to check <c>max_connections</c> and warn.
/// </para>
/// </remarks>
public sealed class RecordingService(LibVlcEngine engine) : IRecordingService, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Entry> _active = new(StringComparer.Ordinal);
    private bool _disposed;

    public IReadOnlyList<RecordingSession> Active =>
        [.. _active.Values.Select(e => e.Session).OrderBy(s => s.StartedAt)];

    public event EventHandler<RecordingChangedEventArgs>? Changed;

    public bool IsRecording(string channelId) =>
        _active.Values.Any(e => e.Session.ChannelId == channelId);

    public RecordingSession Start(
        string channelId, string channelName, string streamUrl, string targetPath,
        string? programmeTitle = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        var media = new Media(engine.LibVlc, new Uri(streamUrl));

        // Single quotes around the destination: a path with spaces otherwise
        // terminates the option early and libvlc writes to a truncated name.
        media.AddOption($":sout=#std{{access=file,mux=ts,dst='{targetPath}'}}");

        // Every elementary stream, not just the first of each type, and keep the
        // chain open across the stream's own discontinuities.
        media.AddOption(":sout-all");
        media.AddOption(":sout-keep");

        // Deliberately NOT :no-audio/:no-video. With a #std chain the stream
        // output IS the output, and disabling the two starves the muxer: the
        // file gets created and then stays at zero bytes. Nothing is displayed
        // regardless, because #std has no display branch.
        media.AddOption(":network-caching=3000");

        var player = new MediaPlayer(engine.LibVlc);

        var session = new RecordingSession
        {
            Id = Guid.NewGuid().ToString(),
            ChannelId = channelId,
            ChannelName = channelName,
            ProgrammeTitle = programmeTitle,
            TargetPath = targetPath,
            StartedAt = DateTimeOffset.UtcNow,
        };

        _active[session.Id] = new Entry(session, player, media);

        player.Play(media);

        Changed?.Invoke(this, new RecordingChangedEventArgs(session, started: true));

        return session;
    }

    public async Task StopAsync(string id)
    {
        if (!_active.TryRemove(id, out var entry))
        {
            return;
        }

        await entry.DisposeAsync().ConfigureAwait(false);

        Changed?.Invoke(this, new RecordingChangedEventArgs(entry.Session, started: false));
    }

    public async Task StopAllAsync()
    {
        foreach (var id in _active.Keys.ToList())
        {
            await StopAsync(id).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAllAsync().ConfigureAwait(false);
    }

    private sealed record Entry(RecordingSession Session, MediaPlayer Player, Media Media)
    {
        public async ValueTask DisposeAsync()
        {
            // Stop blocks until the muxer flushes and the decoder thread joins,
            // which is what closes the .ts cleanly. Doing it on the UI thread is
            // the classic "app freezes when I stop recording".
            await Task.Run(() =>
            {
                try
                {
                    Player.Stop();
                }
                catch (Exception)
                {
                    // Already torn down by a stream that ended on its own.
                }

                Player.Dispose();
                Media.Dispose();
            }).ConfigureAwait(false);
        }
    }
}
