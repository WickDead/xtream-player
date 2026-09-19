using Dapper;
using Microsoft.Data.Sqlite;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Infrastructure.Persistence;

namespace XtreamPlayer.Infrastructure.Epg;

public interface IEpgRepository
{
    Task<EpgRefreshState> GetRefreshStateAsync(ProfileScope scope, CancellationToken ct = default);

    /// <summary>Programmes overlapping a window, for every mapped channel.</summary>
    Task<IReadOnlyDictionary<string, List<EpgProgramme>>> GetWindowAsync(
        ProfileScope scope, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);

    Task<IReadOnlyList<EpgProgramme>> GetForChannelAsync(
        ProfileScope scope, string xmltvChannelId, DateTimeOffset from, DateTimeOffset to,
        CancellationToken ct = default);

    Task<IReadOnlyList<EpgChannel>> GetChannelsAsync(ProfileScope scope, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, EpgChannelMapping>> GetMappingsAsync(
        ProfileScope scope, CancellationToken ct = default);

    Task SaveMappingsAsync(
        ProfileScope scope, IReadOnlyList<EpgChannelMapping> mappings, CancellationToken ct = default);

    /// <summary>Opens a bulk ingest. Nothing is visible until it is committed.</summary>
    Task<IEpgIngest> BeginIngestAsync(ProfileScope scope, CancellationToken ct = default);

    Task PruneAsync(ProfileScope scope, DateTimeOffset olderThan, CancellationToken ct = default);
}

/// <summary>A guide refresh in progress.</summary>
public interface IEpgIngest : IAsyncDisposable
{
    Task AddChannelAsync(EpgChannel channel, CancellationToken ct = default);
    Task AddProgrammeAsync(EpgProgramme programme, CancellationToken ct = default);

    /// <summary>Swaps the new data in. Until this runs, the old guide is untouched.</summary>
    Task CommitAsync(int channelCount, int programmeCount, string? source, CancellationToken ct = default);

    Task FailAsync(string error, CancellationToken ct = default);
}

public sealed class EpgRepository(IDbConnectionFactory connections) : IEpgRepository
{
    public async Task<EpgRefreshState> GetRefreshStateAsync(
        ProfileScope scope, CancellationToken ct = default)
    {
        await using var connection = connections.Open();

        var row = await connection.QuerySingleOrDefaultAsync<RefreshRow>(new CommandDefinition(
            "SELECT * FROM epg_refresh WHERE profile_id = @ProfileId;",
            new { ProfileId = scope.Value },
            cancellationToken: ct));

        return row is null
            ? new EpgRefreshState()
            : new EpgRefreshState
            {
                LastRefresh = row.last_refresh_utc is { } unix and > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(unix)
                    : null,
                ChannelCount = (int)(row.channel_count ?? 0),
                ProgrammeCount = (int)(row.programme_count ?? 0),
                Source = row.source,
                Error = row.error,
            };
    }

    public async Task<IReadOnlyDictionary<string, List<EpgProgramme>>> GetWindowAsync(
        ProfileScope scope, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await using var connection = connections.Open();

        // One ordered query for the whole visible window. For a thousand
        // channels over 30 hours this is roughly 20k rows - small enough to hold
        // in memory, and far cheaper than a query per channel.
        var rows = await connection.QueryAsync<ProgrammeRow>(new CommandDefinition(
            """
            SELECT xmltv_channel_id, start_utc, stop_utc, title, sub_title,
                   description, category, episode_num
            FROM epg_programme
            WHERE profile_id = @ProfileId
              AND stop_utc > @From
              AND start_utc < @To
            ORDER BY xmltv_channel_id, start_utc;
            """,
            new { ProfileId = scope.Value, From = from.ToUnixTimeSeconds(), To = to.ToUnixTimeSeconds() },
            cancellationToken: ct));

        var result = new Dictionary<string, List<EpgProgramme>>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (!result.TryGetValue(row.xmltv_channel_id, out var list))
            {
                list = [];
                result[row.xmltv_channel_id] = list;
            }

            list.Add(Map(row));
        }

        return result;
    }

    public async Task<IReadOnlyList<EpgProgramme>> GetForChannelAsync(
        ProfileScope scope, string xmltvChannelId, DateTimeOffset from, DateTimeOffset to,
        CancellationToken ct = default)
    {
        await using var connection = connections.Open();

        var rows = await connection.QueryAsync<ProgrammeRow>(new CommandDefinition(
            """
            SELECT xmltv_channel_id, start_utc, stop_utc, title, sub_title,
                   description, category, episode_num
            FROM epg_programme
            WHERE profile_id = @ProfileId
              AND xmltv_channel_id = @ChannelId
              AND stop_utc > @From
              AND start_utc < @To
            ORDER BY start_utc;
            """,
            new
            {
                ProfileId = scope.Value,
                ChannelId = xmltvChannelId,
                From = from.ToUnixTimeSeconds(),
                To = to.ToUnixTimeSeconds(),
            },
            cancellationToken: ct));

        return [.. rows.Select(Map)];
    }

    public async Task<IReadOnlyList<EpgChannel>> GetChannelsAsync(
        ProfileScope scope, CancellationToken ct = default)
    {
        await using var connection = connections.Open();

        var rows = await connection.QueryAsync<ChannelRow>(new CommandDefinition(
            "SELECT xmltv_channel_id, display_name, icon FROM epg_channel WHERE profile_id = @ProfileId;",
            new { ProfileId = scope.Value },
            cancellationToken: ct));

        return [.. rows.Select(r => new EpgChannel
        {
            XmltvId = r.xmltv_channel_id,
            DisplayName = r.display_name,
            IconUrl = r.icon,
        })];
    }

    public async Task<IReadOnlyDictionary<string, EpgChannelMapping>> GetMappingsAsync(
        ProfileScope scope, CancellationToken ct = default)
    {
        await using var connection = connections.Open();

        var rows = await connection.QueryAsync<MappingRow>(new CommandDefinition(
            """
            SELECT channel_id, xmltv_channel_id, match_method, is_user_override
            FROM epg_channel_map WHERE profile_id = @ProfileId;
            """,
            new { ProfileId = scope.Value },
            cancellationToken: ct));

        return rows.ToDictionary(
            r => r.channel_id,
            r => new EpgChannelMapping
            {
                ChannelId = r.channel_id,
                XmltvChannelId = r.xmltv_channel_id,
                Method = Enum.TryParse<EpgMatchMethod>(r.match_method, out var method)
                    ? method
                    : EpgMatchMethod.None,
                IsUserOverride = r.is_user_override != 0,
            },
            StringComparer.Ordinal);
    }

    public async Task SaveMappingsAsync(
        ProfileScope scope, IReadOnlyList<EpgChannelMapping> mappings, CancellationToken ct = default)
    {
        await using var connection = connections.Open();
        await using var transaction = await connection.BeginTransactionAsync(ct);

        foreach (var mapping in mappings)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO epg_channel_map
                    (profile_id, channel_id, xmltv_channel_id, match_method, is_user_override)
                VALUES (@ProfileId, @ChannelId, @XmltvId, @Method, @Override)
                ON CONFLICT (profile_id, channel_id) DO UPDATE SET
                    xmltv_channel_id = excluded.xmltv_channel_id,
                    match_method     = excluded.match_method,
                    is_user_override = excluded.is_user_override;
                """,
                new
                {
                    ProfileId = scope.Value,
                    mapping.ChannelId,
                    XmltvId = mapping.XmltvChannelId,
                    Method = mapping.Method.ToString(),
                    Override = mapping.IsUserOverride ? 1 : 0,
                },
                transaction: transaction, cancellationToken: ct));
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<IEpgIngest> BeginIngestAsync(ProfileScope scope, CancellationToken ct = default)
    {
        var connection = connections.Open();
        var ingest = new EpgIngest(connection, scope);
        await ingest.PrepareAsync(ct).ConfigureAwait(false);
        return ingest;
    }

    public async Task PruneAsync(
        ProfileScope scope, DateTimeOffset olderThan, CancellationToken ct = default)
    {
        await using var connection = connections.Open();

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM epg_programme WHERE profile_id = @ProfileId AND stop_utc < @Cutoff;",
            new { ProfileId = scope.Value, Cutoff = olderThan.ToUnixTimeSeconds() },
            cancellationToken: ct));
    }

    private static EpgProgramme Map(ProgrammeRow row) => new()
    {
        XmltvChannelId = row.xmltv_channel_id,
        Start = DateTimeOffset.FromUnixTimeSeconds(row.start_utc),
        Stop = DateTimeOffset.FromUnixTimeSeconds(row.stop_utc),
        Title = row.title ?? "Unknown programme",
        SubTitle = row.sub_title,
        Description = row.description,
        Category = row.category,
        EpisodeNumber = row.episode_num,
    };

    private sealed class ProgrammeRow
    {
        public string xmltv_channel_id { get; set; } = string.Empty;
        public long start_utc { get; set; }
        public long stop_utc { get; set; }
        public string? title { get; set; }
        public string? sub_title { get; set; }
        public string? description { get; set; }
        public string? category { get; set; }
        public string? episode_num { get; set; }
    }

    private sealed class ChannelRow
    {
        public string xmltv_channel_id { get; set; } = string.Empty;
        public string? display_name { get; set; }
        public string? icon { get; set; }
    }

    private sealed class MappingRow
    {
        public string channel_id { get; set; } = string.Empty;
        public string? xmltv_channel_id { get; set; }
        public string? match_method { get; set; }
        public long is_user_override { get; set; }
    }

    private sealed class RefreshRow
    {
        public long? last_refresh_utc { get; set; }
        public long? channel_count { get; set; }
        public long? programme_count { get; set; }
        public string? source { get; set; }
        public string? error { get; set; }
    }
}

/// <summary>
/// A bulk guide ingest, staged so the live guide is never half-written.
/// </summary>
/// <remarks>
/// <para>
/// Rows land in a staging table first and are swapped into place in a single
/// transaction at the end. Writing straight into <c>epg_programme</c> would mean
/// deleting the existing guide before the replacement is complete, so a failure
/// partway through - a dropped connection on a 300 MB download - leaves the user
/// with a guide that is simply gone.
/// </para>
/// <para>
/// Batching matters at this scale: a provider feed is commonly 200k to 1M
/// programmes, and one statement per row without a surrounding transaction
/// would take minutes rather than seconds.
/// </para>
/// </remarks>
internal sealed class EpgIngest(SqliteConnection connection, ProfileScope scope) : IEpgIngest
{
    private const int BatchSize = 20_000;

    private SqliteTransaction? _transaction;
    private int _pending;
    private bool _finished;

    public async Task PrepareAsync(CancellationToken ct)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            PRAGMA temp_store = MEMORY;

            DROP TABLE IF EXISTS epg_programme_staging;

            CREATE TEMP TABLE epg_programme_staging (
                profile_id       TEXT    NOT NULL,
                xmltv_channel_id TEXT    NOT NULL,
                start_utc        INTEGER NOT NULL,
                stop_utc         INTEGER NOT NULL,
                title            TEXT,
                sub_title        TEXT,
                description      TEXT,
                category         TEXT,
                episode_num      TEXT
            );

            DROP TABLE IF EXISTS epg_channel_staging;

            CREATE TEMP TABLE epg_channel_staging (
                profile_id       TEXT NOT NULL,
                xmltv_channel_id TEXT NOT NULL,
                display_name     TEXT,
                icon             TEXT
            );
            """,
            cancellationToken: ct));

        _transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
    }

    public async Task AddChannelAsync(EpgChannel channel, CancellationToken ct = default)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO epg_channel_staging (profile_id, xmltv_channel_id, display_name, icon)
            VALUES (@ProfileId, @Id, @Name, @Icon);
            """,
            new
            {
                ProfileId = scope.Value,
                Id = channel.XmltvId,
                Name = channel.DisplayName,
                Icon = channel.IconUrl,
            },
            transaction: _transaction, cancellationToken: ct));

        await MaybeFlushAsync(ct).ConfigureAwait(false);
    }

    public async Task AddProgrammeAsync(EpgProgramme programme, CancellationToken ct = default)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO epg_programme_staging
                (profile_id, xmltv_channel_id, start_utc, stop_utc, title,
                 sub_title, description, category, episode_num)
            VALUES (@ProfileId, @ChannelId, @Start, @Stop, @Title,
                    @SubTitle, @Description, @Category, @Episode);
            """,
            new
            {
                ProfileId = scope.Value,
                ChannelId = programme.XmltvChannelId,
                Start = programme.Start.ToUnixTimeSeconds(),
                Stop = programme.Stop.ToUnixTimeSeconds(),
                programme.Title,
                programme.SubTitle,
                programme.Description,
                programme.Category,
                Episode = programme.EpisodeNumber,
            },
            transaction: _transaction, cancellationToken: ct));

        await MaybeFlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Commits periodically so the transaction's journal does not grow without
    /// bound on a million-row feed.
    /// </summary>
    private async Task MaybeFlushAsync(CancellationToken ct)
    {
        if (++_pending < BatchSize || _transaction is null)
        {
            return;
        }

        await _transaction.CommitAsync(ct);
        await _transaction.DisposeAsync();

        _transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        _pending = 0;
    }

    public async Task CommitAsync(
        int channelCount, int programmeCount, string? source, CancellationToken ct = default)
    {
        if (_transaction is not null)
        {
            await _transaction.CommitAsync(ct);
            await _transaction.DisposeAsync();
            _transaction = null;
        }

        await using var swap = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        // The swap itself: the old guide is only removed once the replacement is
        // fully staged, and both happen in one transaction.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM epg_programme WHERE profile_id = @ProfileId;

            INSERT OR REPLACE INTO epg_programme
                (profile_id, xmltv_channel_id, start_utc, stop_utc, title,
                 sub_title, description, category, episode_num)
            SELECT profile_id, xmltv_channel_id, start_utc, stop_utc, title,
                   sub_title, description, category, episode_num
            FROM epg_programme_staging;

            DELETE FROM epg_channel WHERE profile_id = @ProfileId;

            INSERT OR REPLACE INTO epg_channel (profile_id, xmltv_channel_id, display_name, icon)
            SELECT profile_id, xmltv_channel_id, display_name, icon
            FROM epg_channel_staging;

            INSERT INTO epg_refresh
                (profile_id, last_refresh_utc, channel_count, programme_count, source, error)
            VALUES (@ProfileId, @Now, @Channels, @Programmes, @Source, NULL)
            ON CONFLICT (profile_id) DO UPDATE SET
                last_refresh_utc = excluded.last_refresh_utc,
                channel_count    = excluded.channel_count,
                programme_count  = excluded.programme_count,
                source           = excluded.source,
                error            = NULL;
            """,
            new
            {
                ProfileId = scope.Value,
                Now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Channels = channelCount,
                Programmes = programmeCount,
                Source = source,
            },
            transaction: swap, cancellationToken: ct));

        await swap.CommitAsync(ct);
        _finished = true;
    }

    /// <summary>
    /// Records a failure without touching the existing guide.
    /// </summary>
    public async Task FailAsync(string error, CancellationToken ct = default)
    {
        if (_transaction is not null)
        {
            await _transaction.RollbackAsync(ct);
            await _transaction.DisposeAsync();
            _transaction = null;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO epg_refresh (profile_id, last_refresh_utc, channel_count, programme_count, source, error)
            VALUES (@ProfileId, NULL, 0, 0, NULL, @Error)
            ON CONFLICT (profile_id) DO UPDATE SET error = excluded.error;
            """,
            new { ProfileId = scope.Value, Error = error },
            cancellationToken: ct));

        _finished = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_transaction is not null)
        {
            // Abandoned without committing: roll back rather than leave a
            // partially staged guide behind.
            if (!_finished)
            {
                await _transaction.RollbackAsync();
            }

            await _transaction.DisposeAsync();
        }

        await connection.DisposeAsync();
    }
}
