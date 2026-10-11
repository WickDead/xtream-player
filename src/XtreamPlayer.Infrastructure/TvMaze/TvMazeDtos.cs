using System.Text.Json.Serialization;

namespace XtreamPlayer.Infrastructure.TvMaze;

/// <summary>
/// One row of <c>/schedule</c> or <c>/schedule/web</c>.
/// </summary>
/// <remarks>
/// The two endpoints return the same episode, but the broadcast schedule puts the
/// show in <c>show</c> and the web schedule in <c>_embedded.show</c>.
/// </remarks>
internal sealed class RawScheduleEpisode
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("season")]
    public int? Season { get; set; }

    [JsonPropertyName("number")]
    public int? Number { get; set; }

    /// <summary>UTC air time, ISO 8601. Absent for some web releases.</summary>
    [JsonPropertyName("airstamp")]
    public string? Airstamp { get; set; }

    [JsonPropertyName("show")]
    public RawScheduleShow? Show { get; set; }

    [JsonPropertyName("_embedded")]
    public RawScheduleEmbedded? Embedded { get; set; }
}

internal sealed class RawScheduleEmbedded
{
    [JsonPropertyName("show")]
    public RawScheduleShow? Show { get; set; }
}

internal sealed class RawScheduleShow
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>First air date, <c>yyyy-MM-dd</c>, or null for unannounced shows.</summary>
    [JsonPropertyName("premiered")]
    public string? Premiered { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }
}
