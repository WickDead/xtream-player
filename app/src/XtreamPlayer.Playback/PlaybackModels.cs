using XtreamPlayer.Core.Models;

namespace XtreamPlayer.Playback;

public enum PlaybackState
{
    Idle,
    Opening,
    Buffering,
    Playing,
    Paused,
    Ended,
    Error,
}

/// <summary>
/// One selectable audio or subtitle track.
/// <paramref name="Id"/> is libvlc's elementary-stream id; -1 means "off"
/// for subtitles and is always present as a synthetic option.
/// </summary>
public sealed record TrackOption(
    int Id,
    string Label,
    string? Language,
    string? Codec,
    bool IsDefault);

public sealed record PlaylistEntry(
    string ContentId,
    string Title,
    string Url,
    int? SeasonNumber = null,
    int? EpisodeNumber = null);

public sealed record PlaybackRequest(
    string Url,
    string Title,
    ContentKind Kind,
    TimeSpan? StartAt = null,
    string? ContentId = null,
    IReadOnlyList<PlaylistEntry>? Playlist = null,
    int PlaylistIndex = 0)
{
    /// <summary>Live streams are never seekable and must not show a seek bar.</summary>
    public bool IsLive => Kind == ContentKind.Live;
}

public sealed class PlaybackStateChangedEventArgs(PlaybackState previous, PlaybackState current)
    : EventArgs
{
    public PlaybackState Previous { get; } = previous;
    public PlaybackState Current { get; } = current;
}

public enum PlaybackErrorKind
{
    /// <summary>libvlc raised EncounteredError and retries are exhausted.</summary>
    DecodeOrConnection,

    /// <summary>The diagnostic probe got 401/403 — credentials, or a connection slot is in use.</summary>
    ProviderRejected,

    /// <summary>The probe reached the stream but libvlc could not decode it.</summary>
    Undecodable,

    /// <summary>libvlc itself could not be loaded — see VlcLocator.</summary>
    EngineUnavailable,
}

public sealed class PlaybackErrorEventArgs(
    PlaybackErrorKind kind,
    string message,
    string? detail = null,
    bool canRetry = true) : EventArgs
{
    public PlaybackErrorKind Kind { get; } = kind;
    public string Message { get; } = message;

    /// <summary>Provider body text or libvlc log tail, for the diagnostics pane.</summary>
    public string? Detail { get; } = detail;

    public bool CanRetry { get; } = canRetry;
}
