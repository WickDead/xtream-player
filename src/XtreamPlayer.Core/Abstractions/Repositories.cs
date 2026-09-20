using XtreamPlayer.Core.Models;

namespace XtreamPlayer.Core.Abstractions;

public interface IProfileRepository
{
    Task<IReadOnlyList<Profile>> ListAsync(CancellationToken ct = default);
    Task<Profile?> GetAsync(ProfileScope scope, CancellationToken ct = default);

    /// <summary>Insert or update by id. Returns the stored profile.</summary>
    Task<Profile> SaveAsync(Profile profile, CancellationToken ct = default);

    /// <summary>Cascades to favorites, resume history, hidden categories and EPG.</summary>
    Task DeleteAsync(ProfileScope scope, CancellationToken ct = default);
}

public interface IFavoritesRepository
{
    Task<IReadOnlyList<FavoriteItem>> ListAsync(ProfileScope scope, CancellationToken ct = default);

    /// <summary>
    /// Idempotent: re-adding an existing (profile, content, kind) returns the
    /// existing row rather than creating a duplicate.
    /// </summary>
    Task<FavoriteItem> AddAsync(
        ProfileScope scope,
        string contentId,
        ContentKind kind,
        string? title,
        CancellationToken ct = default);

    Task RemoveAsync(ProfileScope scope, string contentId, ContentKind kind, CancellationToken ct = default);

    Task RemoveByIdAsync(ProfileScope scope, string favoriteId, CancellationToken ct = default);
}

public interface IResumeRepository
{
    /// <summary>Unfinished items, most recent first. Powers Continue Watching.</summary>
    Task<IReadOnlyList<ResumeItem>> ListContinueWatchingAsync(
        ProfileScope scope, int limit = 20, CancellationToken ct = default);

    Task<ResumeItem?> GetAsync(
        ProfileScope scope, string contentId, ContentKind kind, CancellationToken ct = default);

    Task SaveAsync(ResumeItem item, CancellationToken ct = default);

    Task RemoveAsync(
        ProfileScope scope, string contentId, ContentKind kind, CancellationToken ct = default);
}

public interface IHiddenCategoryRepository
{
    Task<IReadOnlyList<HiddenCategory>> ListAsync(
        ProfileScope scope, CategoryScope categoryScope, CancellationToken ct = default);

    Task HideAsync(
        ProfileScope scope, CategoryScope categoryScope, string categoryId, string? categoryName,
        CancellationToken ct = default);

    Task UnhideAsync(
        ProfileScope scope, CategoryScope categoryScope, string categoryId, CancellationToken ct = default);

    /// <summary>
    /// Repoints a stored row at a new category id after it was matched by name,
    /// so the next lookup is an exact id match again.
    /// </summary>
    Task RepointAsync(
        ProfileScope scope, CategoryScope categoryScope, string oldCategoryId, string newCategoryId,
        CancellationToken ct = default);

    Task ClearAsync(ProfileScope scope, CategoryScope? categoryScope = null, CancellationToken ct = default);
}

/// <summary>Key/value store replacing every localStorage key the old app used.</summary>
public interface ISettingsRepository
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string? value, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, string?>> GetAllAsync(CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
}

/// <summary>Well-known settings keys.</summary>
public static class SettingKeys
{
    public const string ActiveProfileId = "profile.active";
    public const string MigrationV1Completed = "migration.v1.completed";
    public const string DownloadsPath = "downloads.path";
    public const string CatalogTtlHours = "catalog.ttlHours";
    public const string EpgTtlHours = "epg.ttlHours";
    public const string PreferredAudioLanguages = "playback.audioLanguages";
    public const string PreferredSubtitleLanguages = "playback.subtitleLanguages";
    public const string SubtitlesOffByDefault = "playback.subtitlesOff";
    public const string HardwareDecoding = "playback.hardwareDecoding";
    public const string VlcPath = "vlc.path";
    public const string Diagnostics = "diagnostics.enabled";
    public const string Theme = "ui.theme";
}
