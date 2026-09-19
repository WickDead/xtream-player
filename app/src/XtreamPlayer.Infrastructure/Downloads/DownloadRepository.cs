using System.Globalization;
using Dapper;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Infrastructure.Persistence;

namespace XtreamPlayer.Infrastructure.Downloads;

public interface IDownloadRepository
{
    Task<IReadOnlyList<DownloadJob>> ListAsync(ProfileScope scope, CancellationToken ct = default);

    /// <summary>Jobs that were mid-flight when the app last closed.</summary>
    Task<IReadOnlyList<DownloadJob>> ListInterruptedAsync(CancellationToken ct = default);

    Task SaveAsync(DownloadJob job, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task<DownloadJob?> FindAsync(ProfileScope scope, string contentId, ContentKind kind, CancellationToken ct = default);
}

public sealed class DownloadRepository(IDbConnectionFactory connections) : IDownloadRepository
{
    private const string Columns =
        "id, profile_id, kind, content_id, title, source_url, target_path, " +
        "total_bytes, downloaded_bytes, supports_range, state, error, created_at, updated_at";

    public async Task<IReadOnlyList<DownloadJob>> ListAsync(
        ProfileScope scope, CancellationToken ct = default)
    {
        await using var connection = connections.Open();

        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM downloads WHERE profile_id = @ProfileId ORDER BY created_at DESC;",
            new { ProfileId = scope.Value },
            cancellationToken: ct));

        return [.. rows.Select(Map)];
    }

    /// <summary>
    /// Anything left Queued or Downloading was interrupted by the app closing,
    /// since a clean stop leaves a job Paused, Completed, Failed or Cancelled.
    /// </summary>
    public async Task<IReadOnlyList<DownloadJob>> ListInterruptedAsync(CancellationToken ct = default)
    {
        await using var connection = connections.Open();

        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM downloads WHERE state IN ('Queued','Downloading') ORDER BY created_at;",
            cancellationToken: ct));

        return [.. rows.Select(Map)];
    }

    public async Task<DownloadJob?> FindAsync(
        ProfileScope scope, string contentId, ContentKind kind, CancellationToken ct = default)
    {
        await using var connection = connections.Open();

        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"""
            SELECT {Columns} FROM downloads
            WHERE profile_id = @ProfileId AND content_id = @ContentId AND kind = @Kind
            LIMIT 1;
            """,
            new { ProfileId = scope.Value, ContentId = contentId, Kind = kind.ToStorageString() },
            cancellationToken: ct));

        return row is null ? null : Map(row);
    }

    public async Task SaveAsync(DownloadJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        _ = new ProfileScope(job.ProfileId);

        await using var connection = connections.Open();

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO downloads
                (id, profile_id, kind, content_id, title, source_url, target_path,
                 total_bytes, downloaded_bytes, supports_range, state, error,
                 created_at, updated_at)
            VALUES
                (@Id, @ProfileId, @Kind, @ContentId, @Title, @SourceUrl, @TargetPath,
                 @TotalBytes, @DownloadedBytes, @SupportsRange, @State, @Error,
                 @CreatedAt, @UpdatedAt)
            ON CONFLICT (id) DO UPDATE SET
                total_bytes      = excluded.total_bytes,
                downloaded_bytes = excluded.downloaded_bytes,
                supports_range   = excluded.supports_range,
                state            = excluded.state,
                error            = excluded.error,
                target_path      = excluded.target_path,
                updated_at       = excluded.updated_at;
            """,
            new
            {
                job.Id,
                job.ProfileId,
                Kind = job.Kind.ToStorageString(),
                job.ContentId,
                job.Title,
                job.SourceUrl,
                job.TargetPath,
                job.TotalBytes,
                job.DownloadedBytes,
                SupportsRange = job.SupportsRange ? 1 : 0,
                State = job.State.ToString(),
                job.Error,
                CreatedAt = Iso(job.CreatedAt),
                UpdatedAt = Iso(job.UpdatedAt),
            },
            cancellationToken: ct));
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var connection = connections.Open();

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM downloads WHERE id = @Id;",
            new { Id = id },
            cancellationToken: ct));
    }

    private static string Iso(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static DownloadJob Map(Row row) => new()
    {
        Id = row.id,
        ProfileId = row.profile_id,
        Kind = ContentKindExtensions.TryParseStorageString(row.kind, out var kind) ? kind : ContentKind.Vod,
        ContentId = row.content_id,
        Title = row.title,
        SourceUrl = row.source_url,
        TargetPath = row.target_path,
        TotalBytes = row.total_bytes,
        DownloadedBytes = row.downloaded_bytes,
        SupportsRange = row.supports_range != 0,
        State = Enum.TryParse<DownloadState>(row.state, out var state) ? state : DownloadState.Queued,
        Error = row.error,
        CreatedAt = Parse(row.created_at),
        UpdatedAt = Parse(row.updated_at),
    };

    private static DateTimeOffset Parse(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var parsed) ? parsed : DateTimeOffset.MinValue;

    private sealed class Row
    {
        public string id { get; set; } = string.Empty;
        public string profile_id { get; set; } = string.Empty;
        public string kind { get; set; } = string.Empty;
        public string content_id { get; set; } = string.Empty;
        public string title { get; set; } = string.Empty;
        public string source_url { get; set; } = string.Empty;
        public string target_path { get; set; } = string.Empty;
        public long total_bytes { get; set; }
        public long downloaded_bytes { get; set; }
        public long supports_range { get; set; }
        public string state { get; set; } = string.Empty;
        public string? error { get; set; }
        public string? created_at { get; set; }
        public string? updated_at { get; set; }
    }
}
