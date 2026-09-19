using System.Globalization;
using Dapper;
using XtreamPlayer.Core.Abstractions;
using XtreamPlayer.Core.Library;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Infrastructure.Security;

namespace XtreamPlayer.Infrastructure.Persistence;

public sealed class ProfileRepository(
    IDbConnectionFactory connections,
    ICredentialProtector protector) : IProfileRepository
{
    public async Task<IReadOnlyList<Profile>> ListAsync(CancellationToken ct = default)
    {
        await using var connection = connections.Open();

        var rows = await connection.QueryAsync<ProfileRow>(
            new CommandDefinition(
                "SELECT * FROM profiles ORDER BY sort_index, name COLLATE NOCASE;",
                cancellationToken: ct));

        return [.. rows.Select(Map)];
    }

    public async Task<Profile?> GetAsync(ProfileScope scope, CancellationToken ct = default)
    {
        await using var connection = connections.Open();

        var row = await connection.QuerySingleOrDefaultAsync<ProfileRow>(
            new CommandDefinition(
                "SELECT * FROM profiles WHERE id = @Id;",
                new { Id = scope.Value },
                cancellationToken: ct));

        return row is null ? null : Map(row);
    }

    public async Task<Profile> SaveAsync(Profile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var now = DateTimeOffset.UtcNow;
        var isNew = string.IsNullOrWhiteSpace(profile.Id);
        var id = isNew ? Guid.NewGuid().ToString() : profile.Id;

        var stored = profile with
        {
            Id = id,
            ServerUrl = ServerUrl.Normalize(profile.ServerUrl),
            Protection = protector.Scheme,
            CreatedAt = isNew ? now : profile.CreatedAt,
            UpdatedAt = now,
        };

        await using var connection = connections.Open();

        // Upsert by id, and deliberately do NOT touch created_at on update.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO profiles
                (id, name, server_url, username, password_enc, password_protection,
                 status, expires_at, max_connections, preferred_live_format,
                 sort_index, created_at, updated_at)
            VALUES
                (@Id, @Name, @ServerUrl, @Username, @PasswordEnc, @PasswordProtection,
                 @Status, @ExpiresAt, @MaxConnections, @PreferredLiveFormat,
                 @SortIndex, @CreatedAt, @UpdatedAt)
            ON CONFLICT (id) DO UPDATE SET
                name                  = excluded.name,
                server_url            = excluded.server_url,
                username              = excluded.username,
                password_enc          = excluded.password_enc,
                password_protection   = excluded.password_protection,
                status                = excluded.status,
                expires_at            = excluded.expires_at,
                max_connections       = excluded.max_connections,
                preferred_live_format = excluded.preferred_live_format,
                sort_index            = excluded.sort_index,
                updated_at            = excluded.updated_at;
            """,
            new
            {
                stored.Id,
                stored.Name,
                stored.ServerUrl,
                stored.Username,
                PasswordEnc = protector.Protect(stored.Password),
                PasswordProtection = protector.Scheme.ToStorageString(),
                stored.Status,
                stored.ExpiresAt,
                stored.MaxConnections,
                PreferredLiveFormat = stored.PreferredLiveFormat.ToString().ToLowerInvariant(),
                stored.SortIndex,
                CreatedAt = Iso(stored.CreatedAt),
                UpdatedAt = Iso(stored.UpdatedAt),
            },
            cancellationToken: ct));

        return stored;
    }

    public async Task DeleteAsync(ProfileScope scope, CancellationToken ct = default)
    {
        await using var connection = connections.Open();

        // Favorites, resume, hidden categories, prefs, recents, downloads and
        // EPG all cascade via foreign keys.
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM profiles WHERE id = @Id;",
            new { Id = scope.Value },
            cancellationToken: ct));
    }

    private Profile Map(ProfileRow row)
    {
        var protection = PasswordProtectionExtensions.ParseStorageString(row.password_protection);
        var password = row.password_enc is null
            ? string.Empty
            : protector.Unprotect(row.password_enc, protection);

        return new Profile
        {
            Id = row.id,
            Name = row.name,
            ServerUrl = row.server_url,
            Username = row.username,
            // Null means the blob could not be decrypted — almost always a
            // database copied from another machine or user account. Surface it
            // as "re-enter the password", never as a crash.
            Password = password ?? string.Empty,
            PasswordUnavailable = password is null,
            Protection = protection,
            Status = row.status,
            ExpiresAt = row.expires_at,
            MaxConnections = row.max_connections is null ? null : (int)row.max_connections.Value,
            PreferredLiveFormat = ParseFormat(row.preferred_live_format),
            SortIndex = (int)row.sort_index,
            CreatedAt = ParseDate(row.created_at),
            UpdatedAt = ParseDate(row.updated_at),
        };
    }

    private static LiveStreamFormat ParseFormat(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "ts" => LiveStreamFormat.Ts,
            "hls" or "m3u8" => LiveStreamFormat.Hls,
            _ => LiveStreamFormat.Auto,
        };

    private static string Iso(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    /// <summary>Raw column shape, matching the table exactly.</summary>
    private sealed class ProfileRow
    {
        public string id { get; set; } = string.Empty;
        public string name { get; set; } = string.Empty;
        public string server_url { get; set; } = string.Empty;
        public string username { get; set; } = string.Empty;
        public byte[]? password_enc { get; set; }
        public string? password_protection { get; set; }
        public string? status { get; set; }
        public string? expires_at { get; set; }
        public long? max_connections { get; set; }
        public string? preferred_live_format { get; set; }
        public long sort_index { get; set; }
        public string? created_at { get; set; }
        public string? updated_at { get; set; }
    }
}
