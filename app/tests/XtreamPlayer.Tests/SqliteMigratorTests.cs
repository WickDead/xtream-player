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
