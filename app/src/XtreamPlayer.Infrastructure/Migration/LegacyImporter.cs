using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using XtreamPlayer.Core.Abstractions;
using XtreamPlayer.Core.Library;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Infrastructure.Persistence;
using XtreamPlayer.Infrastructure.Security;

namespace XtreamPlayer.Infrastructure.Migration;

/// <summary>A legacy database the user can choose to import from.</summary>
public sealed record LegacyDatabaseInfo
{
    public required string Path { get; init; }
    public required DateTimeOffset ModifiedAt { get; init; }
    public int ProfileCount { get; init; }
    public int FavoriteCount { get; init; }
    public int ResumeCount { get; init; }
    public IReadOnlyList<string> ProfileNames { get; init; } = [];

    /// <summary>Populated when the file exists but could not be read.</summary>
    public string? Error { get; init; }

    public bool IsUsable => Error is null && ProfileCount > 0;
}

public sealed record ImportResult
{
    public int ProfilesImported { get; init; }
    public int FavoritesImported { get; init; }
    public int ResumeImported { get; init; }
    public int HiddenCategoriesImported { get; init; }
    public int DemoRowsSkipped { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// One-time import from the previous Tauri app's SQLite database.
/// </summary>
/// <remarks>
/// The old app also ran a <c>remove_demo_rows</c> DELETE on every single
/// launch, forever, to clean up scaffolding rows. That is not ported: the
/// importer simply skips those ids once, which has the same effect without
/// leaving a permanent startup cost behind.
/// </remarks>
public sealed class LegacyImporter(
    IDbConnectionFactory connections,
    ICredentialProtector protector)
{
    /// <summary>
    /// Scaffolding rows the old app shipped with and then deleted on every
    /// boot. Skipped once here instead.
    /// </summary>
    private static readonly HashSet<string> DemoIds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "fav-1", "fav-2", "cw-1", "cw-2", "primary", "backup",
        };

    /// <summary>
    /// Describes each known legacy database so the user can choose knowingly
    /// rather than have one picked for them. Never modifies the source.
    /// </summary>
    public IReadOnlyList<LegacyDatabaseInfo> DiscoverLegacyDatabases()
    {
        var found = new List<LegacyDatabaseInfo>();

        foreach (var path in DatabasePaths.LegacyDatabaseCandidates())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            found.Add(Describe(path));
        }

        return found;
    }

    public LegacyDatabaseInfo Describe(string path)
    {
        var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);

        try
        {
            // Read-only, and against a copy: the old app may still be running,
            // and an import must never be able to damage the source.
            using var connection = OpenLegacyReadOnly(path);

            var names = connection.Query<string>("SELECT name FROM profiles;").ToList();

            return new LegacyDatabaseInfo
            {
                Path = path,
                ModifiedAt = modified,
                ProfileCount = names.Count,
                FavoriteCount = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM favorites;"),
                ResumeCount = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM resume_history;"),
                ProfileNames = names,
            };
        }
        catch (SqliteException ex)
        {
            return new LegacyDatabaseInfo
            {
                Path = path,
                ModifiedAt = modified,
                Error = ex.Message,
            };
        }
    }

    private static SqliteConnection OpenLegacyReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());

        connection.Open();
        return connection;
    }

    /// <summary>
    /// Imports profiles, favorites and resume history from a legacy database,
    /// plus hidden categories from exported localStorage JSON.
    /// </summary>
    /// <param name="hiddenCategoriesJson">
    /// Optional. The object produced by the old app's devtools console, keyed
    /// live/vod/series. See <see cref="LegacyHiddenCategories"/>.
    /// </param>
    public async Task<ImportResult> ImportAsync(
        string legacyPath,
        string? hiddenCategoriesJson = null,
        CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var profilesImported = 0;
        var favoritesImported = 0;
        var resumeImported = 0;
        var demoSkipped = 0;

        using var legacy = OpenLegacyReadOnly(legacyPath);

        await using var target = connections.Open();
        await using var transaction = await target.BeginTransactionAsync(ct);

        try
        {
            // ---- profiles ----
            var legacyProfiles = legacy.Query<LegacyProfile>(
                "SELECT * FROM profiles;").ToList();

            // Old ids are reused so favorites and resume rows keep pointing at
            // the right provider without a mapping table.
            var knownProfileIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var row in legacyProfiles)
            {
                if (DemoIds.Contains(row.id))
                {
                    demoSkipped++;
                    continue;
                }

                var now = DateTimeOffset.UtcNow;

                await target.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO profiles
                        (id, name, server_url, username, password_enc, password_protection,
                         status, expires_at, max_connections, preferred_live_format,
                         sort_index, created_at, updated_at)
                    VALUES
                        (@Id, @Name, @ServerUrl, @Username, @PasswordEnc, @Protection,
                         @Status, @ExpiresAt, @MaxConnections, 'auto',
                         0, @CreatedAt, @UpdatedAt)
                    ON CONFLICT (id) DO NOTHING;
                    """,
                    new
                    {
                        Id = row.id,
                        Name = string.IsNullOrWhiteSpace(row.name) ? "Imported provider" : row.name,
                        ServerUrl = ServerUrl.Normalize(row.server_url),
                        Username = row.username ?? string.Empty,
                        // Re-protected on the way in: the old app stored these
                        // in plaintext.
                        PasswordEnc = protector.Protect(row.password ?? string.Empty),
                        Protection = protector.Scheme.ToStorageString(),
                        Status = row.status,
                        ExpiresAt = row.expires_at,
                        MaxConnections = row.max_connections,
                        CreatedAt = row.created_at ?? Iso(now),
                        UpdatedAt = Iso(now),
                    },
                    transaction: transaction, cancellationToken: ct));

                knownProfileIds.Add(row.id);
                profilesImported++;
            }

            // A single surviving profile is an unambiguous home for orphaned rows.
            var fallbackProfileId = knownProfileIds.Count == 1 ? knownProfileIds.First() : null;

            // ---- favorites ----
            foreach (var row in legacy.Query<LegacyFavorite>("SELECT * FROM favorites;"))
            {
                if (DemoIds.Contains(row.id))
                {
                    demoSkipped++;
                    continue;
                }

                var profileId = ResolveProfileId(
                    row.profile_id, knownProfileIds, fallbackProfileId, warnings, "favorite", row.title);

                if (profileId is null)
                {
                    continue;
                }

                if (!ContentKindExtensions.TryParseStorageString(row.kind, out var kind))
                {
                    warnings.Add($"Skipped favorite '{row.title}': unrecognised kind '{row.kind}'.");
                    continue;
                }

                // OR IGNORE covers the case the old schema allowed and the new
                // one does not: the same (profile, content, kind) twice.
                await target.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT OR IGNORE INTO favorites
                        (id, profile_id, content_id, kind, title, sort_index, created_at)
                    VALUES (@Id, @ProfileId, @ContentId, @Kind, @Title, 0, @CreatedAt);
                    """,
                    new
                    {
                        Id = row.id,
                        ProfileId = profileId,
                        ContentId = row.content_id,
                        Kind = kind.ToStorageString(),
                        Title = row.title,
                        CreatedAt = row.created_at ?? Iso(DateTimeOffset.UtcNow),
                    },
                    transaction: transaction, cancellationToken: ct));

                favoritesImported++;
            }

            // ---- resume history ----
            foreach (var row in legacy.Query<LegacyResume>("SELECT * FROM resume_history;"))
            {
                if (DemoIds.Contains(row.id))
                {
                    demoSkipped++;
                    continue;
                }

                var profileId = ResolveProfileId(
                    row.profile_id, knownProfileIds, fallbackProfileId, warnings, "resume entry", row.title);

                if (profileId is null)
                {
                    continue;
                }

                if (!ContentKindExtensions.TryParseStorageString(row.kind, out var kind))
                {
                    continue;
                }

                var percent = (int)Math.Clamp(row.progress_percent ?? 0, 0, 100);

                // The old schema stored only a percentage, so absolute position
                // is unknown until the title is played again and its duration
                // is known. Percent is preserved and drives the progress bar.
                await target.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT OR IGNORE INTO resume_history
                        (id, profile_id, content_id, kind, title, detail,
                         position_ms, duration_ms, progress_percent, finished, updated_at)
                    VALUES (@Id, @ProfileId, @ContentId, @Kind, @Title, @Detail,
                            0, 0, @Percent, @Finished, @UpdatedAt);
                    """,
                    new
                    {
                        Id = row.id,
                        ProfileId = profileId,
                        ContentId = row.content_id,
                        Kind = kind.ToStorageString(),
                        Title = row.title,
                        Detail = row.detail,
                        Percent = percent,
                        Finished = percent >= 95 ? 1 : 0,
                        UpdatedAt = row.updated_at ?? Iso(DateTimeOffset.UtcNow),
                    },
                    transaction: transaction, cancellationToken: ct));

                resumeImported++;
            }

            // ---- hidden categories ----
            var hiddenImported = 0;

            if (!string.IsNullOrWhiteSpace(hiddenCategoriesJson))
            {
                hiddenImported = await ImportHiddenCategoriesAsync(
                    target, transaction, hiddenCategoriesJson, knownProfileIds, warnings, ct);
            }

            await target.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO settings (key, value) VALUES (@Key, @Value)
                ON CONFLICT (key) DO UPDATE SET value = excluded.value;
                """,
                new { Key = SettingKeys.MigrationV1Completed, Value = Iso(DateTimeOffset.UtcNow) },
                transaction: transaction, cancellationToken: ct));

            await transaction.CommitAsync(ct);

            return new ImportResult
            {
                ProfilesImported = profilesImported,
                FavoritesImported = favoritesImported,
                ResumeImported = resumeImported,
                HiddenCategoriesImported = hiddenImported,
                DemoRowsSkipped = demoSkipped,
                Warnings = warnings,
            };
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// The old app's hidden categories were display names with no provider and
    /// no category id, so they are applied to every imported profile and matched
    /// by name until the first real lookup repoints them to an id.
    /// </summary>
    private static async Task<int> ImportHiddenCategoriesAsync(
        SqliteConnection target,
        System.Data.Common.DbTransaction transaction,
        string json,
        IReadOnlyCollection<string> profileIds,
        List<string> warnings,
        CancellationToken ct)
    {
        LegacyHiddenCategories parsed;
        try
        {
            parsed = LegacyHiddenCategories.Parse(json);
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            warnings.Add($"Hidden categories were not imported: {ex.Message}");
            return 0;
        }

        if (profileIds.Count == 0)
        {
            warnings.Add("Hidden categories were not imported: no profiles to attach them to.");
            return 0;
        }

        if (profileIds.Count > 1)
        {
            warnings.Add(
                $"The old app shared hidden categories across all providers, so they were "
              + $"applied to all {profileIds.Count} imported profiles. Adjust per provider in Settings.");
        }

        var count = 0;
        var now = Iso(DateTimeOffset.UtcNow);

        foreach (var (scope, names) in parsed.ByScope)
        {
            foreach (var name in names)
            {
                foreach (var profileId in profileIds)
                {
                    // No id is available, so the name doubles as the key. The
                    // resolver's name-match tier finds it, then repoints the row
                    // to the real category id on first use.
                    await target.ExecuteAsync(new CommandDefinition(
                        """
                        INSERT OR IGNORE INTO hidden_categories
                            (profile_id, scope, category_id, category_name, created_at)
                        VALUES (@ProfileId, @Scope, @CategoryId, @CategoryName, @CreatedAt);
                        """,
                        new
                        {
                            ProfileId = profileId,
                            Scope = scope.ToStorageString(),
                            CategoryId = "legacy-name:" + name,
                            CategoryName = name,
                            CreatedAt = now,
                        },
                        transaction: transaction, cancellationToken: ct));

                    count++;
                }
            }
        }

        return count;
    }

    private static string? ResolveProfileId(
        string? rawProfileId,
        HashSet<string> knownProfileIds,
        string? fallbackProfileId,
        List<string> warnings,
        string rowKind,
        string? title)
    {
        if (!string.IsNullOrWhiteSpace(rawProfileId) && knownProfileIds.Contains(rawProfileId))
        {
            return rawProfileId;
        }

        // Rows created before profiles existed, or pointing at a provider that
        // is no longer in the database.
        if (fallbackProfileId is not null)
        {
            return fallbackProfileId;
        }

        warnings.Add(
            $"Skipped {rowKind} '{title}': it belongs to provider '{rawProfileId}', "
          + "which was not imported.");

        return null;
    }

    private static string Iso(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    // Legacy column shapes, matching the old Rust schema exactly.

    private sealed class LegacyProfile
    {
        public string id { get; set; } = string.Empty;
        public string? name { get; set; }
        public string? server_url { get; set; }
        public string? username { get; set; }
        public string? password { get; set; }
        public string? status { get; set; }
        public string? expires_at { get; set; }
        public long? max_connections { get; set; }
        public string? created_at { get; set; }
    }

    private sealed class LegacyFavorite
    {
        public string id { get; set; } = string.Empty;
        public string? profile_id { get; set; }
        public string content_id { get; set; } = string.Empty;
        public string? title { get; set; }
        public string? kind { get; set; }
        public string? created_at { get; set; }
    }

    private sealed class LegacyResume
    {
        public string id { get; set; } = string.Empty;
        public string? profile_id { get; set; }
        public string content_id { get; set; } = string.Empty;
        public string? title { get; set; }
        public string? kind { get; set; }
        public string? detail { get; set; }
        public long? progress_percent { get; set; }
        public string? updated_at { get; set; }
    }
}
