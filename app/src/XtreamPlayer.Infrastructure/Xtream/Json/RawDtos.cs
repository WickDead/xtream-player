using System.Text.Json.Serialization;

namespace XtreamPlayer.Infrastructure.Xtream.Json;

// Raw wire shapes. Every field is nullable and every type is the most permissive
// one the lenient converters can produce, because a provider is free to send
// anything. Mapping to the real domain models happens in XtreamClient.

public sealed class RawAuthResponse
{
    [JsonPropertyName("user_info")]
    public RawUserInfo? UserInfo { get; set; }

    [JsonPropertyName("server_info")]
    public RawServerInfo? ServerInfo { get; set; }
}

public sealed class RawUserInfo
{
    public string? username { get; set; }
    public string? status { get; set; }
    public string? message { get; set; }

    [JsonPropertyName("exp_date")]
    public string? ExpDate { get; set; }

    [JsonPropertyName("is_trial")]
    public string? IsTrial { get; set; }

    [JsonPropertyName("active_cons")]
    public long? ActiveCons { get; set; }

    [JsonPropertyName("max_connections")]
    public long? MaxConnections { get; set; }

    [JsonPropertyName("allowed_output_formats")]
    [JsonConverter(typeof(LenientStringListConverter))]
    public List<string>? AllowedOutputFormats { get; set; }
}

public sealed class RawServerInfo
{
    public string? url { get; set; }
    public string? port { get; set; }

    [JsonPropertyName("https_port")]
    public string? HttpsPort { get; set; }

    [JsonPropertyName("server_protocol")]
    public string? ServerProtocol { get; set; }

    public string? timezone { get; set; }
}

public sealed class RawCategory
{
    [JsonPropertyName("category_id")]
    public string? CategoryId { get; set; }

    [JsonPropertyName("category_name")]
    public string? CategoryName { get; set; }

    [JsonPropertyName("parent_id")]
    public long? ParentId { get; set; }
}

public sealed class RawLiveStream
{
    public string? num { get; set; }
    public string? name { get; set; }

    [JsonPropertyName("stream_id")]
    public string? StreamId { get; set; }

    [JsonPropertyName("stream_icon")]
    public string? StreamIcon { get; set; }

    [JsonPropertyName("epg_channel_id")]
    public string? EpgChannelId { get; set; }

    [JsonPropertyName("category_id")]
    public string? CategoryId { get; set; }

    [JsonPropertyName("tv_archive")]
    public long? TvArchive { get; set; }

    [JsonPropertyName("tv_archive_duration")]
    public long? TvArchiveDuration { get; set; }

    public string? added { get; set; }
}

public sealed class RawVodStream
{
    public string? num { get; set; }
    public string? name { get; set; }

    [JsonPropertyName("stream_id")]
    public string? StreamId { get; set; }

    [JsonPropertyName("stream_icon")]
    public string? StreamIcon { get; set; }

    public string? rating { get; set; }

    [JsonPropertyName("category_id")]
    public string? CategoryId { get; set; }

    [JsonPropertyName("container_extension")]
    public string? ContainerExtension { get; set; }

    [JsonConverter(typeof(UnixSecondsConverter))]
    public DateTimeOffset? added { get; set; }
}

public sealed class RawVodInfoResponse
{
    public RawVodInfo? info { get; set; }

    [JsonPropertyName("movie_data")]
    public RawVodMovieData? MovieData { get; set; }
}

public sealed class RawVodInfo
{
    public string? plot { get; set; }
    public string? cast { get; set; }
    public string? director { get; set; }
    public string? genre { get; set; }
    public string? rating { get; set; }
    public string? duration { get; set; }

    [JsonPropertyName("movie_image")]
    public string? MovieImage { get; set; }

    [JsonPropertyName("backdrop_path")]
    [JsonConverter(typeof(LenientStringListConverter))]
    public List<string>? BackdropPath { get; set; }

    [JsonPropertyName("releasedate")]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("youtube_trailer")]
    public string? YoutubeTrailer { get; set; }

    [JsonPropertyName("video")]
    public RawVideoInfo? Video { get; set; }

    [JsonPropertyName("bitrate")]
    public long? Bitrate { get; set; }
}

public sealed class RawVideoInfo
{
    public long? width { get; set; }
    public long? height { get; set; }

    [JsonPropertyName("codec_name")]
    public string? CodecName { get; set; }
}

public sealed class RawVodMovieData
{
    public string? name { get; set; }

    [JsonPropertyName("stream_id")]
    public string? StreamId { get; set; }

    [JsonPropertyName("container_extension")]
    public string? ContainerExtension { get; set; }
}

public sealed class RawSeries
{
    public string? num { get; set; }
    public string? name { get; set; }

    [JsonPropertyName("series_id")]
    public string? SeriesId { get; set; }

    public string? cover { get; set; }
    public string? plot { get; set; }
    public string? cast { get; set; }
    public string? genre { get; set; }
    public string? rating { get; set; }

    [JsonPropertyName("category_id")]
    public string? CategoryId { get; set; }

    [JsonPropertyName("last_modified")]
    [JsonConverter(typeof(UnixSecondsConverter))]
    public DateTimeOffset? LastModified { get; set; }
}

public sealed class RawSeriesInfoResponse
{
    public RawSeriesInfo? info { get; set; }

    [JsonConverter(typeof(LenientListConverter<RawSeason>))]
    public List<RawSeason> seasons { get; set; } = [];

    [JsonConverter(typeof(LenientEpisodeMapConverter<RawEpisode>))]
    public Dictionary<string, List<RawEpisode>> episodes { get; set; } = [];
}

public sealed class RawSeriesInfo
{
    public string? name { get; set; }
    public string? plot { get; set; }
    public string? cast { get; set; }
    public string? genre { get; set; }
    public string? cover { get; set; }
    public string? rating { get; set; }

    [JsonPropertyName("backdrop_path")]
    [JsonConverter(typeof(LenientStringListConverter))]
    public List<string>? BackdropPath { get; set; }
}

public sealed class RawSeason
{
    [JsonPropertyName("season_number")]
    public long? SeasonNumber { get; set; }

    public string? name { get; set; }
    public string? overview { get; set; }
    public string? cover { get; set; }

    [JsonPropertyName("episode_count")]
    public long? EpisodeCount { get; set; }
}

public sealed class RawEpisode
{
    public string? id { get; set; }
    public string? title { get; set; }

    [JsonPropertyName("episode_num")]
    public long? EpisodeNum { get; set; }

    public long? season { get; set; }

    [JsonPropertyName("container_extension")]
    public string? ContainerExtension { get; set; }

    /// <summary>
    /// Preferred over a constructed URL when non-empty; some providers serve
    /// episodes from a different host entirely.
    /// </summary>
    [JsonPropertyName("direct_source")]
    public string? DirectSource { get; set; }

    public RawEpisodeInfo? info { get; set; }

    [JsonConverter(typeof(UnixSecondsConverter))]
    public DateTimeOffset? added { get; set; }
}

public sealed class RawEpisodeInfo
{
    public string? plot { get; set; }
    public string? duration { get; set; }

    [JsonPropertyName("movie_image")]
    public string? MovieImage { get; set; }

    [JsonPropertyName("releasedate")]
    public string? ReleaseDate { get; set; }
}

public sealed class RawEpgListing
{
    public string? id { get; set; }

    [JsonPropertyName("epg_id")]
    public string? EpgId { get; set; }

    public string? title { get; set; }
    public string? description { get; set; }
    public string? lang { get; set; }

    public string? start { get; set; }
    public string? end { get; set; }

    [JsonPropertyName("start_timestamp")]
    public string? StartTimestamp { get; set; }

    [JsonPropertyName("stop_timestamp")]
    public string? StopTimestamp { get; set; }

    [JsonPropertyName("channel_id")]
    public string? ChannelId { get; set; }
}

public sealed class RawShortEpgResponse
{
    [JsonPropertyName("epg_listings")]
    [JsonConverter(typeof(LenientListConverter<RawEpgListing>))]
    public List<RawEpgListing> EpgListings { get; set; } = [];
}
