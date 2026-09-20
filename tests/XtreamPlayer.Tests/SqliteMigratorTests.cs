using System.Reflection;
using Microsoft.Data.Sqlite;
using XtreamPlayer.Infrastructure.Persistence;
using Xunit;

namespace XtreamPlayer.Tests;

public sealed class SqliteMigratorTests
{
    private static SqliteConnection OpenInMemory()
    {
        // A named shared in-memory database stays alive as long as this
        // connection is open, so the migrator and the assertions see the same db.
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    [Fact]
    public void FreshDatabaseMigratesToLatest()
    {
        using var connection = OpenInMemory();
        var migrator = new SqliteMigrator();

        var applied = migrator.Migrate(connection);
        var expected = migrator.LoadScripts().Select(s => s.Version).ToArray();

        Assert.NotEmpty(expected);
        Assert.Equal(expected, applied);
        Assert.Equal(expected[^1], migrator.GetCurrentVersion(connection));
    }

    [Fact]
    public void MigratingTwiceIsANoOp()
    {
        using var connection = OpenInMemory();
        var migrator = new SqliteMigrator();

        var first = migrator.Migrate(connection);
        var second = migrator.Migrate(connection);

        Assert.NotEmpty(first);
        Assert.Empty(second);
    }

    [Fact]
    public void CreatesEveryExpectedTable()
    {
        using var connection = OpenInMemory();
        new SqliteMigrator().Migrate(connection);

        string[] expected =
        [
            "profiles", "favorites", "resume_history", "hidden_categories",
            "settings", "playback_prefs", "recent_channels", "downloads",
            "epg_channel", "epg_programme", "epg_channel_map", "epg_refresh",
            "schema_version",
        ];

        var actual = Query(connection, "SELECT name FROM sqlite_master WHERE type='table';");

        foreach (var table in expected)
        {
            Assert.Contains(table, actual);
        }
    }

    /// <summary>
    /// The old schema deduped favorites on (profile_id, content_id) only. Xtream
    /// live and VOD stream ids share one numeric namespace, so favoriting live
    /// channel 1234 silently blocked favoriting movie 1234.
    /// </summary>
    [Fact]
    public void FavoritesUniquenessIncludesKind()
    {
        using var connection = OpenInMemory();
        new SqliteMigrator().Migrate(connection);
        InsertProfile(connection, "p1");

        InsertFavorite(connection, "f1", "p1", "1234", "live");

        // Same content id, different kind: must be allowed.
        InsertFavorite(connection, "f2", "p1", "1234", "vod");

        // Same content id AND kind: must be rejected.
        Assert.Throws<SqliteException>(() =>
            InsertFavorite(connection, "f3", "p1", "1234", "live"));

        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM favorites;"));
    }

    /// <summary>
    /// Deleting a provider must not strand its favorites, resume rows or EPG.
    /// </summary>
    [Fact]
    public void DeletingAProfileCascades()
    {
        using var connection = OpenInMemory();
        new SqliteMigrator().Migrate(connection);
        InsertProfile(connection, "p1");
        InsertProfile(connection, "p2");
        InsertFavorite(connection, "f1", "p1", "1", "live");
        InsertFavorite(connection, "f2", "p2", "1", "live");

        Execute(connection, "DELETE FROM profiles WHERE id = 'p1';");

        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM favorites;"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM favorites WHERE profile_id='p2';"));
    }

    [Fact]
    public void FavoritesRequireAnExistingProfile()
    {
        using var connection = OpenInMemory();
        new SqliteMigrator().Migrate(connection);

        // profile_id NOT NULL + FK is what makes the old "no WHERE profile_id"
        // bug impossible to reintroduce at the schema level.
        Assert.Throws<SqliteException>(() =>
            InsertFavorite(connection, "f1", "ghost", "1", "live"));
    }

    [Fact]
    public void FailedMigrationRollsBackAndLeavesVersionUnchanged()
    {
        using var connection = OpenInMemory();

        var broken = new SqliteMigrator(
            typeof(SqliteMigratorTests).Assembly,
            "XtreamPlayer.Tests.BrokenMigrations.");

        var ex = Assert.Throws<MigrationException>(() => broken.Migrate(connection));
        Assert.Contains("rolled back", ex.Message, StringComparison.OrdinalIgnoreCase);

        // 001 created a table then 002 failed; 001 must have committed and 002
        // must have left nothing behind.
        Assert.Equal(1, broken.GetCurrentVersion(connection));

        var tables = Query(connection, "SELECT name FROM sqlite_master WHERE type='table';");
        Assert.Contains("good_table", tables);
        Assert.DoesNotContain("half_built", tables);
    }

    [Fact]
    public void ScriptsAreOrderedAndUniquelyVersioned()
    {
        var scripts = new SqliteMigrator().LoadScripts();

        Assert.Equal(
            scripts.Select(s => s.Version).Order().ToArray(),
            scripts.Select(s => s.Version).ToArray());

        Assert.Equal(
            scripts.Select(s => s.Version).Distinct().Count(),
            scripts.Count);
    }

    // ---- helpers --------------------------------------------------------

    private static void InsertProfile(SqliteConnection c, string id) => Execute(c,
        $"""
        INSERT INTO profiles (id, name, server_url, username, created_at, updated_at)
        VALUES ('{id}', '{id}', 'http://example.invalid', 'u', '2026-01-01', '2026-01-01');
        """);

    private static void InsertFavorite(
        SqliteConnection c, string id, string profileId, string contentId, string kind) => Execute(c,
        $"""
        INSERT INTO favorites (id, profile_id, content_id, kind, title, created_at)
        VALUES ('{id}', '{profileId}', '{contentId}', '{kind}', 't', '2026-01-01');
        """);

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    private static List<string> Query(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        var results = new List<string>();
        while (reader.Read())
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }
}

/// <summary>
/// The data directory is not guaranteed to be empty. A discarded prototype left
/// a database at exactly the path this app uses, with colliding table names but
/// a different shape.
/// </summary>
public sealed class ForeignDatabaseGuardTests
{
    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "xp-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Create(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
        SqliteConnection.ClearPool(connection);
    }

    [Fact]
    public void LeavesAMissingFileAlone()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "nothing.sqlite");

        Assert.Null(ForeignDatabaseGuard.QuarantineIfForeign(path));

        Directory.Delete(directory, true);
    }

    [Fact]
    public void LeavesAnUnrelatedDatabaseAlone()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "other.sqlite");
        Create(path, "CREATE TABLE unrelated (id INTEGER PRIMARY KEY);");

        Assert.Null(ForeignDatabaseGuard.QuarantineIfForeign(path));
        Assert.True(File.Exists(path));

        Directory.Delete(directory, true);
    }

    [Fact]
    public void LeavesOurOwnMigratedDatabaseAlone()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, DatabasePaths.DatabaseFileName);

        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            new SqliteMigrator().Migrate(connection);
        }

        SqliteConnection.ClearAllPools();

        Assert.Null(ForeignDatabaseGuard.QuarantineIfForeign(path));

        Directory.Delete(directory, true);
    }

    [Fact]
    public void QuarantinesAPrototypeDatabaseWithCollidingTables()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, DatabasePaths.DatabaseFileName);

        // The real prototype's shape: its own versioning table and integer ids.
        Create(path, """
            CREATE TABLE schema_versions (id INTEGER PRIMARY KEY, version INTEGER);
            CREATE TABLE profiles (id INTEGER PRIMARY KEY, name TEXT);
            CREATE TABLE favorites (id INTEGER PRIMARY KEY);
            """);

        var result = ForeignDatabaseGuard.QuarantineIfForeign(path);

        Assert.NotNull(result);
        Assert.Contains("profiles", result.Reason, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(result.BackupPath));

        // Nothing is ever deleted.
        Assert.Contains(".bak.", result.BackupPath, StringComparison.Ordinal);

        Directory.Delete(directory, true);
    }

    /// <summary>
    /// The migration runner creates schema_version before the migration loop and
    /// commits it outside the per-script transaction. So a FAILED run against a
    /// foreign database leaves an empty version table behind. Treating that as
    /// proof of ownership would make the failure permanent: the guard skips,
    /// migration 001 collides again, and the app can never start.
    /// </summary>
    [Fact]
    public void QuarantinesEvenAfterAFailedRunStampedAnEmptyVersionTable()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, DatabasePaths.DatabaseFileName);

        Create(path, """
            CREATE TABLE profiles (id INTEGER PRIMARY KEY, name TEXT);
            CREATE TABLE schema_version (
                version INTEGER NOT NULL PRIMARY KEY, name TEXT, applied_at TEXT NOT NULL);
            """);

        var result = ForeignDatabaseGuard.QuarantineIfForeign(path);

        Assert.NotNull(result);
        Assert.Contains("no applied migration", result.Reason, StringComparison.Ordinal);
        Assert.False(File.Exists(path));

        Directory.Delete(directory, true);
    }

    [Fact]
    public void MovesWalSidecarsWithTheDatabase()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, DatabasePaths.DatabaseFileName);

        Create(path, "CREATE TABLE profiles (id INTEGER PRIMARY KEY);");

        // Sidecars belong to the file they were written for; leaving them
        // behind would corrupt the fresh database.
        File.WriteAllText(path + "-wal", "stale");
        File.WriteAllText(path + "-shm", "stale");

        var result = ForeignDatabaseGuard.QuarantineIfForeign(path);

        Assert.NotNull(result);
        Assert.False(File.Exists(path + "-wal"));
        Assert.False(File.Exists(path + "-shm"));
        Assert.True(File.Exists(result.BackupPath + "-wal"));

        Directory.Delete(directory, true);
    }
}
