namespace XtreamPlayer.Core.Models;

/// <summary>A provider category, in the provider's own ordering.</summary>
public sealed record Category
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>
    /// Index in the provider's own ordering, preserved so the "API order" sort
    /// works. Ports <c>ordered_category_map</c> (xtream.rs:118).
    /// </summary>
    public int Order { get; init; }

    public int ItemCount { get; set; }
}

public sealed record LiveChannel
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string CategoryId { get; init; }
    public string? CategoryName { get; init; }
    public int CategoryOrder { get; init; }

    /// <summary>Provider's XMLTV id. Frequently empty or wrong; see the EPG matcher.</summary>
    public string? EpgChannelId { get; init; }

    public string? IconUrl { get; init; }

    /// <summary>The provider's own channel number, when it supplies one.</summary>
    public int? Number { get; init; }

    /// <summary>Built from the profile's preferred format; see XtreamUrlBuilder.</summary>
    public required string StreamUrl { get; init; }

    public bool HasArchive { get; init; }
    public int ArchiveDays { get; init; }
}

public sealed record VodItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string CategoryId { get; init; }
    public string? CategoryName { get; init; }
    public int CategoryOrder { get; init; }
    public string? PosterUrl { get; init; }

    /// <summary>Normalized to one decimal place; null when the provider sends 0 or junk.</summary>
    public string? Rating { get; init; }

    public DateTimeOffset? Added { get; init; }

    /// <summary>Needed to build the stream URL; providers vary (mp4, mkv, avi).</summary>
    public string ContainerExtension { get; init; } = "mp4";

    public required string StreamUrl { get; init; }
}

public sealed record VodDetail
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Plot { get; init; }
    public string? Cast { get; init; }
    public string? Director { get; init; }
    public string? Genre { get; init; }
    public string? ReleaseDate { get; init; }
    public string? Rating { get; init; }
    public string? DurationLabel { get; init; }
    public string? PosterUrl { get; init; }
    public string? BackdropUrl { get; init; }
    public string? YoutubeTrailer { get; init; }
    public string? Quality { get; init; }
    public long? FileSizeBytes { get; init; }
}

public sealed record SeriesItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string CategoryId { get; init; }
    public string? CategoryName { get; init; }
    public int CategoryOrder { get; init; }
    public string? PosterUrl { get; init; }
    public string? Plot { get; init; }
    public string? Genre { get; init; }
    public string? Rating { get; init; }
    public DateTimeOffset? LastModified { get; init; }
}

public sealed record SeriesSeason
{
    public required int Number { get; init; }
    public string? Name { get; init; }
    public string? Overview { get; init; }
    public string? PosterUrl { get; init; }
    public int EpisodeCount { get; init; }
}

public sealed record SeriesEpisode
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required int SeasonNumber { get; init; }
    public required int EpisodeNumber { get; init; }
    public string? Plot { get; init; }
    public string? StillUrl { get; init; }
    public string? DurationLabel { get; init; }
    public string ContainerExtension { get; init; } = "mp4";
    public required string StreamUrl { get; init; }
    public DateTimeOffset? Added { get; init; }
}

public sealed record SeriesDetail
{
    public required string SeriesId { get; init; }
    public required string Name { get; init; }
    public string? Plot { get; init; }
    public string? Cast { get; init; }
    public string? Genre { get; init; }
    public string? PosterUrl { get; init; }
    public string? BackdropUrl { get; init; }
    public string? Rating { get; init; }

    public IReadOnlyList<SeriesSeason> Seasons { get; init; } = [];

    /// <summary>Keyed by season number.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<SeriesEpisode>> EpisodesBySeason { get; init; } =
        new Dictionary<int, IReadOnlyList<SeriesEpisode>>();

    public int EpisodeCount => EpisodesBySeason.Values.Sum(e => e.Count);
}

/// <summary>One programme from the provider's short EPG endpoint.</summary>
public sealed record EpgEntry
{
    public required string ChannelId { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset Stop { get; init; }

    /// <summary>Already base64-decoded; see XtreamText.DecodeEpgText.</summary>
    public required string Title { get; init; }

    public string? Description { get; init; }

    public bool IsNowPlaying(DateTimeOffset at) => at >= Start && at < Stop;
}

/// <summary>Account state from the provider's authentication response.</summary>
public sealed record AccountInfo
{
    public required string Status { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public int? MaxConnections { get; init; }
    public int ActiveConnections { get; init; }
    public bool IsTrial { get; init; }

    /// <summary>
    /// Drives the live container choice. A provider offering both ts and m3u8
    /// gets ts, which libvlc decodes natively.
    /// </summary>
    public IReadOnlyList<string> AllowedOutputFormats { get; init; } = [];

    public string? ServerUrl { get; init; }
    public string? ServerPort { get; init; }
    public string? TimeZone { get; init; }

    public bool IsActive =>
        string.Equals(Status, "Active", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A loaded catalog for one profile.</summary>
public sealed record Catalog<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public required IReadOnlyList<Category> Categories { get; init; }
    public DateTimeOffset LoadedAt { get; init; } = DateTimeOffset.UtcNow;

    public static Catalog<T> Empty { get; } = new()
    {
        Items = [],
        Categories = [],
    };
}
