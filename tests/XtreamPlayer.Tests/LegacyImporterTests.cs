using Microsoft.Data.Sqlite;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Infrastructure.Migration;
using Xunit;

namespace XtreamPlayer.Tests;

/// <summary>
/// Builds a database with the previous Rust app's exact schema
/// (src-tauri/src/persistence/sqlite.rs:10), including its quirks: plaintext
/// passwords, a nullable profile_id, and no uniqueness on favorites.
/// </summary>
internal static class LegacyDatabaseBuilder
{
    public static string Create(string directory, Action<SqliteConnection> seed)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "legacy.sqlite");

        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE profiles (
                    id TEXT PRIMARY KEY, name TEXT NOT NULL, server_url TEXT NOT NULL,
                    username TEXT NOT NULL, password TEXT NOT NULL, status TEXT NOT NULL,
                    expires_at TEXT NOT NULL, max_connections INTEGER NOT NULL,
                    created_at TEXT NOT NULL, updated_at TEXT NOT NULL);

                CREATE TABLE favorites (
                    id TEXT PRIMARY KEY, profile_id TEXT, content_id TEXT NOT NULL,
                    title TEXT NOT NULL, kind TEXT NOT NULL, created_at TEXT NOT NULL);

                CREATE TABLE resume_history (
                    id TEXT PRIMARY KEY, profile_id TEXT, content_id TEXT NOT NULL,
                    title TEXT NOT NULL, kind TEXT NOT NULL, detail TEXT NOT NULL,
                    progress_percent INTEGER NOT NULL, updated_at TEXT NOT NULL);
                """;
            command.ExecuteNonQuery();
        }

        seed(connection);
        SqliteConnection.ClearPool(connection);
        return path;
    }

    public static void AddProfile(
        SqliteConnection c, string id, string name, string password = "plaintext-pw") =>
        Execute(c,
            """
            INSERT INTO profiles
                (id, name, server_url, username, password, status, expires_at,
                 max_connections, created_at, updated_at)
            VALUES ($id, $name, 'provider.example', 'user', $pw, 'Active', '1964974277', 2,
                    '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
            """,
            ("$id", id), ("$name", name), ("$pw", password));

    public static void AddFavorite(
        SqliteConnection c, string id, string? profileId, string contentId,
        string title, string kind) =>
        Execute(c,
            """
            INSERT INTO favorites (id, profile_id, content_id, title, kind, created_at)
            VALUES ($id, $pid, $cid, $title, $kind, '2026-01-01T00:00:00Z');
            """,
            ("$id", id), ("$pid", (object?)profileId ?? DBNull.Value),
            ("$cid", contentId), ("$title", title), ("$kind", kind));

    public static void AddResume(
        SqliteConnection c, string id, string? profileId, string contentId,
        string title, string kind, int percent) =>
        Execute(c,
            """
            INSERT INTO resume_history
                (id, profile_id, content_id, title, kind, detail, progress_percent, updated_at)
            VALUES ($id, $pid, $cid, $title, $kind, '', $pct, '2026-01-01T00:00:00Z');
            """,
            ("$id", id), ("$pid", (object?)profileId ?? DBNull.Value),
            ("$cid", contentId), ("$title", title), ("$kind", kind), ("$pct", percent));

    private static void Execute(
        SqliteConnection c, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = c.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }
}

public sealed class LegacyImporterTests
{
    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "xp-legacy-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ImportsProfilesFavoritesAndResume()
    {
        var dir = TempDir();
        var legacy = LegacyDatabaseBuilder.Create(dir, c =>
        {
            LegacyDatabaseBuilder.AddProfile(c, "p-wicked", "Wicked");
            LegacyDatabaseBuilder.AddFavorite(c, "f1", "p-wicked", "604", "Sportsnet 360", "live");
            LegacyDatabaseBuilder.AddFavorite(c, "f2", "p-wicked", "696", "Seinfeld", "series");
            LegacyDatabaseBuilder.AddResume(c, "r1", "p-wicked", "1148189", "Driver's Ed", "vod", 42);
        });

        using var db = new TempDatabase();
        var importer = new LegacyImporter(db.Connections, db.Protector);

        var result = await importer.ImportAsync(legacy);

        Assert.Equal(1, result.ProfilesImported);
        Assert.Equal(2, result.FavoritesImported);
        Assert.Equal(1, result.ResumeImported);

        var profile = Assert.Single(await db.Profiles.ListAsync());
        Assert.Equal("Wicked", profile.Name);
        // The old app stored the server URL unnormalized.
        Assert.Equal("http://provider.example", profile.ServerUrl);
        Assert.Equal("plaintext-pw", profile.Password);

        var scope = new ProfileScope(profile.Id);
        Assert.Equal(2, (await db.Favorites.ListAsync(scope)).Count);

        var resume = await db.Resume.GetAsync(scope, "1148189", ContentKind.Vod);
        Assert.NotNull(resume);
        Assert.Equal(42, resume.ProgressPercent);

        Directory.Delete(dir, true);
    }

    /// <summary>
    /// The old app deleted these scaffolding rows on every single launch,
    /// forever. Skipping them once here has the same effect.
    /// </summary>
    [Fact]
    public async Task SkipsDemoScaffoldingRows()
    {
        var dir = TempDir();
        var legacy = LegacyDatabaseBuilder.Create(dir, c =>
        {
            LegacyDatabaseBuilder.AddProfile(c, "p1", "Real");
            LegacyDatabaseBuilder.AddProfile(c, "primary", "Demo Primary");
            LegacyDatabaseBuilder.AddFavorite(c, "fav-1", "p1", "1", "Demo", "live");
            LegacyDatabaseBuilder.AddFavorite(c, "keep", "p1", "2", "Real Favorite", "live");
        });

        using var db = new TempDatabase();
        var result = await new LegacyImporter(db.Connections, db.Protector).ImportAsync(legacy);

        Assert.Equal(1, result.ProfilesImported);
        Assert.Equal(1, result.FavoritesImported);
        Assert.Equal(2, result.DemoRowsSkipped);

        Directory.Delete(dir, true);
    }

    /// <summary>
    /// profile_id was nullable in the old schema. With exactly one provider the
    /// owner is unambiguous, so orphans are adopted rather than dropped.
    /// </summary>
    [Fact]
    public async Task AdoptsOrphanedRowsWhenOnlyOneProfileExists()
    {
        var dir = TempDir();
        var legacy = LegacyDatabaseBuilder.Create(dir, c =>
        {
            LegacyDatabaseBuilder.AddProfile(c, "p1", "Only");
            LegacyDatabaseBuilder.AddFavorite(c, "f1", null, "604", "Orphan", "live");
        });

        using var db = new TempDatabase();
        var result = await new LegacyImporter(db.Connections, db.Protector).ImportAsync(legacy);

        Assert.Equal(1, result.FavoritesImported);

        var profile = Assert.Single(await db.Profiles.ListAsync());
        Assert.Single(await db.Favorites.ListAsync(new ProfileScope(profile.Id)));

        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task WarnsRatherThanGuessesWhenOwnershipIsAmbiguous()
    {
        var dir = TempDir();
        var legacy = LegacyDatabaseBuilder.Create(dir, c =>
        {
            LegacyDatabaseBuilder.AddProfile(c, "p1", "First");
            LegacyDatabaseBuilder.AddProfile(c, "p2", "Second");
            LegacyDatabaseBuilder.AddFavorite(c, "f1", null, "604", "Orphan", "live");
        });

        using var db = new TempDatabase();
        var result = await new LegacyImporter(db.Connections, db.Protector).ImportAsync(legacy);

        Assert.Equal(0, result.FavoritesImported);
        Assert.Contains(result.Warnings, w => w.Contains("Orphan", StringComparison.Ordinal));

        Directory.Delete(dir, true);
    }

    /// <summary>
    /// The old schema allowed (profile, content) with differing kinds AND exact
    /// duplicates. The new UNIQUE constraint must not make the import throw.
    /// </summary>
    [Fact]
    public async Task ToleratesDuplicateFavoritesTheOldSchemaAllowed()
    {
        var dir = TempDir();
        var legacy = LegacyDatabaseBuilder.Create(dir, c =>
        {
            LegacyDatabaseBuilder.AddProfile(c, "p1", "Only");
            LegacyDatabaseBuilder.AddFavorite(c, "f1", "p1", "1234", "Channel", "live");
            LegacyDatabaseBuilder.AddFavorite(c, "f2", "p1", "1234", "Channel again", "live");
            LegacyDatabaseBuilder.AddFavorite(c, "f3", "p1", "1234", "Movie", "vod");
        });

        using var db = new TempDatabase();
        var result = await new LegacyImporter(db.Connections, db.Protector).ImportAsync(legacy);

        var profile = Assert.Single(await db.Profiles.ListAsync());
        var favorites = await db.Favorites.ListAsync(new ProfileScope(profile.Id));

        // The exact duplicate collapses; the different kind survives.
        Assert.Equal(2, favorites.Count);
        Assert.Equal(3, result.FavoritesImported);

        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task IsIdempotent()
    {
        var dir = TempDir();
        var legacy = LegacyDatabaseBuilder.Create(dir, c =>
        {
            LegacyDatabaseBuilder.AddProfile(c, "p1", "Only");
            LegacyDatabaseBuilder.AddFavorite(c, "f1", "p1", "604", "Sportsnet", "live");
        });

        using var db = new TempDatabase();
        var importer = new LegacyImporter(db.Connections, db.Protector);

        await importer.ImportAsync(legacy);
        await importer.ImportAsync(legacy);

        var profile = Assert.Single(await db.Profiles.ListAsync());
        Assert.Single(await db.Favorites.ListAsync(new ProfileScope(profile.Id)));

        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ImportsHiddenCategoriesFromExportedJson()
    {
        var dir = TempDir();
        var legacy = LegacyDatabaseBuilder.Create(dir, c =>
            LegacyDatabaseBuilder.AddProfile(c, "p1", "Only"));

        // Exactly the shape devtools produces: values are themselves JSON strings.
        const string exported = """
            {"live":"[\"Sports CA\",\"Kids\"]","vod":null,"series":"[\"Anime\"]"}
            """;

        using var db = new TempDatabase();
        var result = await new LegacyImporter(db.Connections, db.Protector)
            .ImportAsync(legacy, exported);

        Assert.Equal(3, result.HiddenCategoriesImported);

        var profile = Assert.Single(await db.Profiles.ListAsync());
        var scope = new ProfileScope(profile.Id);

        var live = await db.Hidden.ListAsync(scope, CategoryScope.Live);
        Assert.Equal(2, live.Count);
        Assert.Contains(live, h => h.CategoryName == "Sports CA");
        Assert.Single(await db.Hidden.ListAsync(scope, CategoryScope.Series));
        Assert.Empty(await db.Hidden.ListAsync(scope, CategoryScope.Vod));

        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task DescribesALegacyDatabaseWithoutModifyingIt()
    {
        var dir = TempDir();
        var legacy = LegacyDatabaseBuilder.Create(dir, c =>
        {
            LegacyDatabaseBuilder.AddProfile(c, "p1", "Wicked");
            LegacyDatabaseBuilder.AddProfile(c, "p2", "Terea");
            LegacyDatabaseBuilder.AddFavorite(c, "f1", "p1", "604", "Sportsnet", "live");
        });

        var before = File.GetLastWriteTimeUtc(legacy);

        using var db = new TempDatabase();
        var info = new LegacyImporter(db.Connections, db.Protector).Describe(legacy);

        Assert.Null(info.Error);
        Assert.Equal(2, info.ProfileCount);
        Assert.Equal(1, info.FavoriteCount);
        Assert.Contains("Wicked", info.ProfileNames);
        Assert.True(info.IsUsable);
        Assert.Equal(before, File.GetLastWriteTimeUtc(legacy));

        await Task.CompletedTask;
        Directory.Delete(dir, true);
    }
}

public sealed class LegacyHiddenCategoriesTests
{
    [Fact]
    public void ParsesTheDoubleEncodedDevtoolsShape()
    {
        var parsed = LegacyHiddenCategories.Parse(
            """{"live":"[\"Sports\",\"Kids\"]","vod":"[\"Horror\"]"}""");

        Assert.Equal(2, parsed.ByScope[CategoryScope.Live].Count);
        Assert.Single(parsed.ByScope[CategoryScope.Vod]);
        Assert.Equal(3, parsed.TotalCount);
    }

    [Fact]
    public void ParsesPlainArraysToo()
    {
        var parsed = LegacyHiddenCategories.Parse("""{"live":["Sports","Kids"]}""");

        Assert.Equal(2, parsed.ByScope[CategoryScope.Live].Count);
    }

    [Fact]
    public void IgnoresNullAndMissingScopes()
    {
        var parsed = LegacyHiddenCategories.Parse("""{"live":null}""");

        Assert.Equal(0, parsed.TotalCount);
    }

    [Fact]
    public void DeduplicatesIgnoringCase()
    {
        var parsed = LegacyHiddenCategories.Parse("""{"live":["Sports","sports","SPORTS"]}""");

        Assert.Single(parsed.ByScope[CategoryScope.Live]);
    }

    [Fact]
    public void TreatsABareStringAsOneCategoryName()
    {
        var parsed = LegacyHiddenCategories.Parse("""{"live":"Sports CA"}""");

        Assert.Equal("Sports CA", Assert.Single(parsed.ByScope[CategoryScope.Live]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyInputYieldsNothing(string input) =>
        Assert.Equal(0, LegacyHiddenCategories.Parse(input).TotalCount);

    [Fact]
    public void RejectsAnUnexpectedShape() =>
        Assert.Throws<FormatException>(() => LegacyHiddenCategories.Parse("[1,2,3]"));
}
