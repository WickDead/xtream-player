namespace XtreamPlayer.Core.Models;

/// <summary>
/// The three kinds of content an Xtream provider exposes.
/// Mirrors the <c>ContentKind</c> union in the previous app's
/// <c>src/types/xtream.ts</c> so imported rows keep their meaning.
/// </summary>
public enum ContentKind
{
    Live,
    Vod,
    Series,
}

public static class ContentKindExtensions
{
    /// <summary>
    /// The lowercase wire/storage form ("live", "vod", "series"). The previous
    /// app persisted these exact strings in the <c>favorites.kind</c> and
    /// <c>resume_history.kind</c> columns, so the importer depends on them.
    /// </summary>
    public static string ToStorageString(this ContentKind kind) => kind switch
    {
        ContentKind.Live => "live",
        ContentKind.Vod => "vod",
        ContentKind.Series => "series",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static bool TryParseStorageString(string? value, out ContentKind kind)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "live": kind = ContentKind.Live; return true;
            case "vod": kind = ContentKind.Vod; return true;
            case "series": kind = ContentKind.Series; return true;
            default: kind = default; return false;
        }
    }
}
