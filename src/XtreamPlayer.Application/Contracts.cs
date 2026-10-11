using XtreamPlayer.Core.Models;

namespace XtreamPlayer.Application;

// Backend catalog responses use opaque artwork IDs. Direct browser mode uses HTTPS artwork URLs.
// Provider login records are stored separately from these catalog models.
public sealed record ProviderSummary(string Id, string Name, string Status, int? MaxConnections);
public sealed record SaveProvider(string Name, string ServerUrl, string Username, string Password);
public sealed record PairRequest(string Code, string DeviceName);
public sealed record PairResponse(string Token, string DeviceId);
public sealed record DeviceSummary(string Id, string Name, DateTimeOffset CreatedAt);
public sealed record MediaItem(string Id, string Name, string CategoryId, int CategoryOrder,
    string Kind, string? ArtworkId = null, DateTimeOffset? Added = null, string? SeriesId = null,
    bool HasArchive = false, int ArchiveDays = 0);
public sealed record BrowseCatalog(List<MediaItem> Items, List<Category> Categories, DateTimeOffset LoadedAt);
public sealed record MediaDetail(string Name, string? Plot, string? Genre, string? Rating,
    List<MediaItem> Episodes);
public sealed record PlayRequest(string ProviderId, string Kind, string ContentId,
    string? SeriesId = null, DateTimeOffset? Start = null, int? DurationMinutes = null,
    double PositionSeconds = 0, int? AudioTrack = null, int? SubtitleTrack = null);
public sealed record MediaTrack(int Index, string Type, string Language, string Title, string Codec);
public sealed record PlaybackSession(string Id, string Url, double OffsetSeconds, double DurationSeconds,
    List<MediaTrack> Tracks, PlaybackCapabilities Capabilities);
public sealed record PlaybackCapabilities(bool Seek, bool AudioSelection, bool SubtitleSelection,
    bool SystemVolume, bool ExternalSubtitles);
public sealed record CreateJob(PlayRequest Media, bool Recording = false, int RecordingMinutes = 60);
public sealed record MediaJob(string Id, string ProviderId, string Title, string State, bool Recording,
    long Bytes, long TotalBytes, string? Error, DateTimeOffset CreatedAt, PlayRequest Media,
    int RecordingMinutes = 60);
public sealed record MediaFile(string Id, string Url, long Bytes, string Version, string Title);
public sealed record DiscoveryResult(List<string> NewSeriesIds);
public sealed record LocalProgress(MediaItem Item, double Position, double Duration, DateTimeOffset UpdatedAt);
public sealed record LocalLibrary
{
    public HashSet<string> Favorites { get; init; } = [];
    public HashSet<string> HiddenCategories { get; init; } = [];
    public Dictionary<string, string> HiddenCategoryNames { get; init; } = [];
    public List<string> RecentChannels { get; init; } = [];
    public Dictionary<string, LocalProgress> Progress { get; init; } = [];
}

public interface IDeviceStorage
{
    ValueTask<T?> GetAsync<T>(string key);
    ValueTask SetAsync<T>(string key, T value);
    ValueTask RemoveAsync(string key);
}
public interface IMediaJobs
{
    Task<IReadOnlyList<MediaJob>> ListAsync(CancellationToken ct = default);
    Task<MediaJob> CreateAsync(CreateJob request, CancellationToken ct = default);
    Task ChangeAsync(string id, string action, CancellationToken ct = default);
}
public interface IRemotePlayback
{
    Task<PlaybackSession> OpenAsync(PlayRequest request, CancellationToken ct = default);
    Task CloseAsync(string id, CancellationToken ct = default);
}
