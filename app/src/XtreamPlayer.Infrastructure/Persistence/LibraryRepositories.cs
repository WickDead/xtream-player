using System.Globalization;
using Dapper;
using XtreamPlayer.Core.Abstractions;
using XtreamPlayer.Core.Models;

namespace XtreamPlayer.Infrastructure.Persistence;

internal static class SqlDate
{
    public static string Iso(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset Parse(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
}

public sealed class FavoritesRepository(IDbConnectionFactory connections) : IFavoritesRepository
{
    public async Task<IReadOnlyList<FavoriteItem>> ListAsync(
        ProfileScope scope, CancellationToken ct = default)
    {
        await using var connection = connections.Open();

        // The WHERE profile_id the old list_favorites was missing.
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            """
            SELECT id, profile_id, content_id, kind, title, sort_index, created_at
            FROM favorites
            WHERE profile_id = @ProfileId
            ORDER BY sort_index, created_at DESC;
            """,
            new { ProfileId = scope.Value },
            cancellationToken: ct));

        return [.. rows.Select(Map)];
    }

    public async Task<FavoriteItem> AddAsync(
        ProfileScope scope,
        string contentId,
        ContentKind kind,
        string? title,
        CancellationToken ct = default)
    {
        await using var connection = connections.Open();
        var kindText = kind.ToStorageString();

        var existing = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            """
            SELECT id, profile_id, content_id, kind, title, sort_index, created_at
            FROM favorites
            WHERE profile_id = @ProfileId AND content_id = @ContentId AND kind = @Kind;
            """,
            new { ProfileId = scope.Value, ContentId = contentId, Kind = kindText },
            cancellationToken: ct));

        if (existing is not null)
        {
            return Map(existing);
        }

        var item = new FavoriteItem
        {
            Id = Guid.NewGuid().ToString(),
            ProfileId = scope.Value,
            ContentId = contentId,
            Kind = kind,
            Title = title,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO favorites (id, profile_id, content_id, kind, title, sort_index, created_at)
            VALUES (@Id, @ProfileId, @ContentId, @Kind, @Title, @SortIndex, @CreatedAt);
            """,
            new
            {
                item.Id,
                item.ProfileId,
                item.ContentId,
                Kind = kindText,
                item.Title,
                item.SortIndex,
                CreatedAt = SqlDate.Iso(item.CreatedAt),
            },
            cancellationToken: ct));

        return item;
    }

    public async Task RemoveAsync(
        ProfileScope scope, string contentId, ContentKind kind, CancellationToken ct = default)
    {
        await using var connection = connections.Open();

        await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM favorites
            WHERE profile_id = @ProfileId AND content_id = @ContentId AND kind = @Kind;
            """,
            new { ProfileId = scope.Value, ContentId = contentId, Kind = kind.ToStorageString() },
            cancellationToken: ct));
    }

    public async Task RemoveByIdAsync(
        ProfileScope scope, string favoriteId, CancellationToken ct = default)
    {
        await using var connection = connections.Open();

        // Scoped even though the id is a primary key: a stale id from another
        // profile must not delete across providers.
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM favorites WHERE id = @Id AND profile_id = @ProfileId;",
            new { Id = favoriteId, ProfileId = scope.Value },
            cancellationToken: ct));
    }

    private static FavoriteItem Map(Row row) => new()
    {
        Id = row.id,
        ProfileId = row.profile_id,
        ContentId = row.content_id,
        Kind = ContentKindExtensions.TryParseStorageString(row.kind, out var kind)
            ? kind
            : ContentKind.Vod,
        Title = row.title,
        SortIndex = (int)row.sort_index,
        CreatedAt = SqlDate.Parse(row.created_at),
    };

    private sealed class Row
    {
        public string id { get; set; } = string.Empty;
        public string profile_id { get; set; } = string.Empty;
        public string content_id { get; set; } = string.Empty;
        public string kind { get; set; } = string.Empty;
        public string? title { get; set; }
        public long sort_index { get; set; }
        public string? created_at { get; set; }
    }
}

public sealed class ResumeRepository(IDbConnectionFactory connections) : IResumeRepository
{
    public async Task<IReadOnlyList<ResumeItem>> ListContinueWatchingAsync(
        ProfileScope scope, int limit = 20, CancellationToken ct = default)
    {
        await using var connection = connections.Open();

        // Live is excluded: it has no meaningful resume position. Recency for
        // live lives in recent_channels instead.
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            """
            SELECT * FROM resume_history
            WHERE profile_id = @ProfileId AND finished = 0 AND kind <> 'live'
            ORDER BY updated_at DESC
            LIMIT @Limit;
            """,
            new { ProfileId = scope.Value, Limit = limit },
            cancellationToken: ct));

        return [.. rows.Select(Map)];
    }

    public async Task<ResumeItem?> GetAsync(
        ProfileScope scope, string contentId, ContentKind kind, CancellationToken ct = default)
    {
        await using var connection = connections.Open();

        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            """
            SELECT * FROM resume_history
            WHERE profile_id = @ProfileId AND content_id = @ContentId AND kind = @Kind;
            """,
            new { ProfileId = scope.Value, ContentId = contentId, Kind = kind.ToStorageString() },
            cancellationToken: ct));

        return row is null ? null : Map(row);
    }

    public async Task SaveAsync(ResumeItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        // Guard at the write, not just the call site: an unscoped row here is
        // exactly the corruption ProfileScope exists to prevent.
        _ = new ProfileScope(item.ProfileId);

        var percent = item.Duration > TimeSpan.Zero
            ? (int)Math.Clamp(item.Position / item.Duration * 100, 0, 100)
            : item.ProgressPercent;

        await using var connection = connections.Open();

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO resume_history
                (id, profile_id, content_id, kind, title, detail, position_ms, duration_ms,
                 progress_percent, finished, series_id, season_number, episode_number, updated_at)
            VALUES
                (@Id, @ProfileId, @ContentId, @Kind, @Title, @Detail, @PositionMs, @DurationMs,
                 @ProgressPercent, @Finished, @SeriesId, @SeasonNumber, @EpisodeNumber, @UpdatedAt)
            ON CONFLICT (profile_id, content_id, kind) DO UPDATE SET
                title            = excluded.title,
                detail           = excluded.detail,
                position_ms      = excluded.position_ms,
                duration_ms      = excluded.duration_ms,
                progress_percent = excluded.progress_percent,
                finished         = excluded.finished,
                series_id        = excluded.series_id,
                season_number    = excluded.season_number,
                episode_number   = excluded.episode_number,
                updated_at       = excluded.updated_at;
            """,
            new
            {
                Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString() : item.Id,
                item.ProfileId,
                item.ContentId,
                Kind = item.Kind.ToStorageString(),
                item.Title,
                item.Detail,
                PositionMs = (long)item.Position.TotalMilliseconds,
                DurationMs = (long)item.Duration.TotalMilliseconds,
                ProgressPercent = percent,
                Finished = item.Finished ? 1 : 0,
                item.SeriesId,
                item.SeasonNumber,
                item.EpisodeNumber,
                UpdatedAt = SqlDate.Iso(item.UpdatedAt == default ? DateTimeOffset.UtcNow : item.UpdatedAt),
            },
            cancellationToken: ct));
    }

    public async Task RemoveAsync(
        ProfileScope scope, string contentId, ContentKind kind, CancellationToken ct = default)
    {
        await using var connection = connections.Open();

        await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM resume_history
            WHERE profile_id = @ProfileId AND content_id = @ContentId AND kind = @Kind;
            """,
            new { ProfileId = scope.Value, ContentId = contentId, Kind = kind.ToStorageString() },
            cancellationToken: ct));
    }

    private static ResumeItem Map(Row row) => new()
    {
        Id = row.id,
        ProfileId = row.profile_id,
        ContentId = row.content_id,
        Kind = ContentKindExtensions.TryParseStorageString(row.kind, out var kind)
            ? kind
            : ContentKind.Vod,
        Title = row.title,
        Detail = row.detail,
        Position = TimeSpan.FromMilliseconds(row.position_ms),
        Duration = TimeSpan.FromMilliseconds(row.duration_ms),
        ProgressPercent = (int)row.progress_percent,
        Finished = row.finished != 0,
        SeriesId = row.series_id,
        SeasonNumber = row.season_number is null ? null : (int)row.season_number.Value,
        EpisodeNumber = row.episode_number is null ? null : (int)row.episode_number.Value,
        UpdatedAt = SqlDate.Parse(row.updated_at),
    };

    private sealed class Row
    {
        public string id { get; set; } = string.Empty;
        public string profile_id { get; set; } = string.Empty;
        public string content_id { get; set; } = string.Empty;
        public string kind { get; set; } = string.Empty;
        public string? title { get; set; }
        public string? detail { get; set; }
        public long position_ms { get; set; }
        public long duration_ms { get; set; }
        public long progress_percent { get; set; }
        public long finished { get; set; }
        public string? series_id { get; set; }
        public long? season_number { get; set; }
        public long? episode_number { get; set; }
        public string? updated_at { get; set; }
    }
}

public sealed class SettingsRepository(IDbConnectionFactory connections) : ISettingsRepository
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var connection = connections.Open();

        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT value FROM settings WHERE key = @Key;",
            new { Key = key },
            cancellationToken: ct));
    }

    public async Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        await using var connection = connections.Open();

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO settings (key, value) VALUES (@Key, @Value)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value;
            """,
            new { Key = key, Value = value },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyDictionary<string, string?>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = connections.Open();

        var rows = await connection.QueryAsync<(string key, string? value)>(
            new CommandDefinition("SELECT key, value FROM settings;", cancellationToken: ct));

        return rows.ToDictionary(r => r.key, r => r.value, StringComparer.Ordinal);
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        await using var connection = connections.Open();

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM settings WHERE key = @Key;",
            new { Key = key },
            cancellationToken: ct));
    }
}
