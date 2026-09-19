namespace XtreamPlayer.Core.Models;

/// <summary>
/// A favorited item.
/// </summary>
/// <remarks>
/// Carries <see cref="ProfileId"/>, which the old app's DTO did not, even though
/// the column existed — the omission is how favorites ended up leaking between
/// providers. It also carries <see cref="Title"/> so the Favorites view can be
/// rendered from this table directly and show items the current catalog no
/// longer contains as "unavailable" rather than silently dropping them.
/// </remarks>
public sealed record FavoriteItem
{
    public required string Id { get; init; }
    public required string ProfileId { get; init; }
    public required string ContentId { get; init; }
    public required ContentKind Kind { get; init; }
    public string? Title { get; init; }
    public int SortIndex { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>A partially watched item, for Continue Watching and resume.</summary>
public sealed record ResumeItem
{
    public required string Id { get; init; }
    public required string ProfileId { get; init; }
    public required string ContentId { get; init; }
    public required ContentKind Kind { get; init; }
    public string? Title { get; init; }

    /// <summary>Free-text secondary line, e.g. "S02E05 - The Parking Garage".</summary>
    public string? Detail { get; init; }

    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Kept in sync with <see cref="Position"/> for rows imported from the old
    /// schema, which stored only a percentage.
    /// </summary>
    public int ProgressPercent { get; init; }

    public bool Finished { get; init; }

    public string? SeriesId { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Which catalog a category belongs to.</summary>
public enum CategoryScope
{
    Live,
    Vod,
    Series,
}

public static class CategoryScopeExtensions
{
    public static string ToStorageString(this CategoryScope scope) => scope switch
    {
        CategoryScope.Live => "live",
        CategoryScope.Vod => "vod",
        CategoryScope.Series => "series",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
    };

    public static bool TryParseStorageString(string? value, out CategoryScope scope)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "live": scope = CategoryScope.Live; return true;
            case "vod": scope = CategoryScope.Vod; return true;
            case "series": scope = CategoryScope.Series; return true;
            default: scope = default; return false;
        }
    }
}

/// <summary>
/// A category the user has hidden.
/// </summary>
/// <remarks>
/// Both id and name are stored. Matching by id is exact; the name is the
/// fallback when a provider reassigns ids, and the row self-heals to the new id
/// when the name matches. The old app stored display names only, in
/// localStorage, shared across every provider.
/// </remarks>
public sealed record HiddenCategory
{
    public required string ProfileId { get; init; }
    public required CategoryScope Scope { get; init; }
    public required string CategoryId { get; init; }
    public string? CategoryName { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// A per-title track preference.
/// </summary>
/// <remarks>
/// Deliberately keyed on language and track name rather than an
/// elementary-stream id: Phase 0 showed libvlc rebuilds the audio ES with a
/// fresh id when switching HLS alternate renditions, so a stored id is
/// meaningless on the next play.
/// </remarks>
public sealed record PlaybackPreference
{
    public required string ProfileId { get; init; }
    public required string ContentId { get; init; }
    public string? AudioLanguage { get; init; }
    public string? AudioTrackName { get; init; }
    public string? SubtitleLanguage { get; init; }
    public string? SubtitleTrackName { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>A recently watched live channel. Live has no resume, only recency.</summary>
public sealed record RecentChannel
{
    public required string ProfileId { get; init; }
    public required string ChannelId { get; init; }
    public string? Title { get; init; }
    public DateTimeOffset WatchedAt { get; init; }
}
