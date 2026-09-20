using Microsoft.Data.Sqlite;

namespace XtreamPlayer.Infrastructure.Persistence;

public sealed record QuarantineResult(string OriginalPath, string BackupPath, string Reason);

/// <summary>
/// Refuses to migrate a database this app did not create.
/// </summary>
/// <remarks>
/// <para>
/// The chosen data directory is not guaranteed to be empty. A discarded
/// prototype left a <c>xtream-player.sqlite</c> at exactly this path with
/// colliding table names, integer profile ids and its own
/// <c>schema_versions</c> table. Running migration 001 against it fails with
/// "table profiles already exists" and the app cannot start.
/// </para>
/// <para>
/// Adopting such a file is not safe: the tables collide by name but not by
/// shape, so every later query would misbehave in ways that look like data
/// corruption. Instead it is moved aside, with its WAL sidecars, and a fresh
/// database is created. Nothing is ever deleted.
/// </para>
/// </remarks>
public static class ForeignDatabaseGuard
{
    /// <summary>Tables migration 001 creates. A collision on any of these is fatal.</summary>
    private static readonly string[] OwnedTables =
    [
        "profiles", "favorites", "resume_history", "hidden_categories",
        "settings", "playback_prefs", "recent_channels", "downloads",
    ];

    private const string VersionTable = "schema_version";

    /// <summary>
    /// Moves an incompatible database aside so a fresh one can be created.
    /// </summary>
    /// <returns>Details of the move, or null when the file is usable as-is.</returns>
    public static QuarantineResult? QuarantineIfForeign(string databaseFile)
    {
        if (!File.Exists(databaseFile) || new FileInfo(databaseFile).Length == 0)
        {
            return null;
        }

        string? reason;
        try
        {
            reason = Inspect(databaseFile);
        }
        finally
        {
            // The file must be fully released before it can be renamed.
            SqliteConnection.ClearAllPools();
        }

        if (reason is null)
        {
            return null;
        }

        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var backupPath = $"{databaseFile}.bak.{timestamp}";

        File.Move(databaseFile, backupPath);

        // WAL and shared-memory sidecars belong to the file they were written
        // for; leaving them behind would corrupt the fresh database.
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = databaseFile + suffix;
            if (File.Exists(sidecar))
            {
                File.Move(sidecar, backupPath + suffix);
            }
        }

        return new QuarantineResult(databaseFile, backupPath, reason);
    }

    private static long CountAppliedMigrations(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {VersionTable};";
            return Convert.ToInt64(command.ExecuteScalar(),
                System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (SqliteException)
        {
            // A table of that name with an unexpected shape is not ours either.
            return 0;
        }
    }

    /// <summary>
    /// Returns why the database is unusable, or null when it is ours or empty.
    /// </summary>
    private static string? Inspect(string databaseFile)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());

        try
        {
            connection.Open();
        }
        catch (SqliteException ex)
        {
            return $"the file could not be opened as a SQLite database ({ex.SqliteErrorCode})";
        }

        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }

        // Ours only if the version table exists AND records an applied
        // migration. An empty version table is not proof of ownership: the
        // runner creates it before the migration loop and commits that outside
        // the per-script transaction, so a previous FAILED run against a
        // foreign database leaves the table behind with no rows. Treating that
        // as ownership makes the failure permanent — the guard skips, migration
        // 001 collides again, and the app can never start.
        if (tables.Contains(VersionTable) && CountAppliedMigrations(connection) > 0)
        {
            return null;
        }

        // Nothing of ours in it, so migration 001 has a clean run.
        var collisions = OwnedTables.Where(tables.Contains).ToArray();
        if (collisions.Length == 0)
        {
            return null;
        }

        // Someone else's tables under our names.
        var versionState = tables.Contains(VersionTable)
            ? $"its {VersionTable} table records no applied migration"
            : $"it has no {VersionTable} table";

        return $"{versionState} but it already defines {string.Join(", ", collisions)}, "
             + "so it was created by a different application or an earlier prototype";
    }
}
