namespace XtreamPlayer.Core.Models;

/// <summary>A channel as declared by the XMLTV feed.</summary>
public sealed record EpgChannel
{
    public required string XmltvId { get; init; }
    public string? DisplayName { get; init; }
    public string? IconUrl { get; init; }
}

/// <summary>
/// One programme.
/// </summary>
/// <remarks>
/// Times are absolute. The previous app kept EPG start/stop as the raw provider
/// strings, which cannot be range-queried at all — the reason it could only ever
/// show "now/next" from a per-channel request and never a real guide.
/// </remarks>
public sealed record EpgProgramme
{
    public required string XmltvChannelId { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset Stop { get; init; }
    public required string Title { get; init; }
    public string? SubTitle { get; init; }
    public string? Description { get; init; }
    public string? Category { get; init; }
    public string? EpisodeNumber { get; init; }

    public TimeSpan Duration => Stop - Start;

    public bool IsNowPlaying(DateTimeOffset at) => at >= Start && at < Stop;

    public double ProgressAt(DateTimeOffset at)
    {
        var total = Duration.TotalSeconds;
        return total <= 0 ? 0 : Math.Clamp((at - Start).TotalSeconds / total, 0, 1);
    }
}

/// <summary>How a live channel was matched to an XMLTV id.</summary>
public enum EpgMatchMethod
{
    /// <summary>The provider's own epg_channel_id matched exactly.</summary>
    ExactId,

    /// <summary>Same id ignoring case and surrounding whitespace.</summary>
    NormalizedId,

    /// <summary>Display names matched once quality and region tokens were stripped.</summary>
    DisplayName,

    /// <summary>The user chose it.</summary>
    UserOverride,

    /// <summary>Nothing matched; this channel falls back to the short-EPG endpoint.</summary>
    None,
}

public sealed record EpgChannelMapping
{
    public required string ChannelId { get; init; }
    public string? XmltvChannelId { get; init; }
    public EpgMatchMethod Method { get; init; }
    public bool IsUserOverride { get; init; }

    public bool IsMatched => !string.IsNullOrEmpty(XmltvChannelId);
}

/// <summary>The outcome of the last guide refresh, for the header and Settings.</summary>
public sealed record EpgRefreshState
{
    public DateTimeOffset? LastRefresh { get; init; }
    public int ChannelCount { get; init; }
    public int ProgrammeCount { get; init; }
    public string? Source { get; init; }
    public string? Error { get; init; }

    public bool HasData => ProgrammeCount > 0;

    public TimeSpan? Age => LastRefresh is { } at ? DateTimeOffset.UtcNow - at : null;
}
