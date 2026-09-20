using XtreamPlayer.Core.Models;
using XtreamPlayer.Infrastructure.Migration;
using Xunit;

namespace XtreamPlayer.Tests;

/// <summary>
/// Runs the importer against a real database from the previous app.
/// </summary>
/// <remarks>
/// <para>
/// Opt-in, because it needs a real file. Point <c>XP_LEGACY_DB</c> at a legacy
/// database and it runs; otherwise every test here is skipped.
/// </para>
/// <para>
/// Synthetic fixtures cannot cover what real data does: providers that were
/// deleted, favorites whose content no longer resolves, rows written by older
/// versions of the schema. This is the check that the import is actually safe
/// to run on someone's library.
/// </para>
/// <para>
/// The source file is copied first and opened read-only, so running this can
/// never damage the original.
/// </para>
/// </remarks>
public sealed class LegacyImportIntegrationTests
{
    private const string PathVariable = "XP_LEGACY_DB";

    private static string? LegacyPath
    {
        get
        {
            var path = Environment.GetEnvironmentVariable(PathVariable);
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
        }
    }

    private static string CopyToTemp(string source)
    {
        var directory = Path.Combine(Path.GetTempPath(), "xp-legacy-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var destination = Path.Combine(directory, "legacy.sqlite");
        File.Copy(source, destination);

        // WAL sidecars carry committed data the main file may not have yet.
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(source + suffix))
            {
                File.Copy(source + suffix, destination + suffix);
            }
        }

        return destination;
    }

    [SkippableFact]
    public void DescribesTheRealDatabase()
    {
        Skip.If(LegacyPath is null, $"Set {PathVariable} to run this.");

        var copy = CopyToTemp(LegacyPath!);
        using var db = new TempDatabase();

        var info = new LegacyImporter(db.Connections, db.Protector).Describe(copy);

        Assert.Null(info.Error);
        Assert.True(info.ProfileCount > 0, "Expected at least one provider profile.");

        Console.WriteLine($"profiles  : {info.ProfileCount} ({string.Join(", ", info.ProfileNames)})");
        Console.WriteLine($"favorites : {info.FavoriteCount}");
        Console.WriteLine($"resume    : {info.ResumeCount}");
    }

    [SkippableFact]
    public async Task ImportsTheRealDatabaseWithoutLoss()
    {
        Skip.If(LegacyPath is null, $"Set {PathVariable} to run this.");

        var copy = CopyToTemp(LegacyPath!);
        using var db = new TempDatabase();
        var importer = new LegacyImporter(db.Connections, db.Protector);

        var before = importer.Describe(copy);
        var result = await importer.ImportAsync(copy);

        Console.WriteLine($"imported  : {result.ProfilesImported} profiles, "
                        + $"{result.FavoritesImported} favorites, {result.ResumeImported} resume");
        Console.WriteLine($"demo rows skipped: {result.DemoRowsSkipped}");

        foreach (var warning in result.Warnings)
        {
            Console.WriteLine($"warning   : {warning}");
        }

        // Every non-demo row must land somewhere or be explained by a warning.
        Assert.Equal(before.ProfileCount, result.ProfilesImported + result.DemoRowsSkipped);

        var profiles = await db.Profiles.ListAsync();
        Assert.Equal(result.ProfilesImported, profiles.Count);

        var totalFavorites = 0;
        foreach (var profile in profiles)
        {
            var scope = new ProfileScope(profile.Id);
            var favorites = await db.Favorites.ListAsync(scope);
            totalFavorites += favorites.Count;

            Console.WriteLine($"  {profile.Name,-10} {favorites.Count,3} favorites  "
                            + $"url={profile.ServerUrl}  pwd={(profile.Password.Length > 0 ? "recovered" : "MISSING")}");

            // Credentials must survive the round trip through encryption, or
            // every stream URL built from this profile will fail.
            Assert.False(profile.PasswordUnavailable);
            Assert.NotEmpty(profile.Password);
            Assert.StartsWith("http", profile.ServerUrl, StringComparison.Ordinal);
        }

        Assert.True(totalFavorites > 0, "Expected favorites to survive the import.");

        Directory.Delete(Path.GetDirectoryName(copy)!, recursive: true);
    }

    [SkippableFact]
    public async Task ImportingTheRealDatabaseTwiceChangesNothing()
    {
        Skip.If(LegacyPath is null, $"Set {PathVariable} to run this.");

        var copy = CopyToTemp(LegacyPath!);
        using var db = new TempDatabase();
        var importer = new LegacyImporter(db.Connections, db.Protector);

        await importer.ImportAsync(copy);

        var profilesAfterFirst = (await db.Profiles.ListAsync()).Count;
        var favoritesAfterFirst = await CountFavoritesAsync(db);

        await importer.ImportAsync(copy);

        Assert.Equal(profilesAfterFirst, (await db.Profiles.ListAsync()).Count);
        Assert.Equal(favoritesAfterFirst, await CountFavoritesAsync(db));

        Directory.Delete(Path.GetDirectoryName(copy)!, recursive: true);
    }

    private static async Task<int> CountFavoritesAsync(TempDatabase db)
    {
        var total = 0;
        foreach (var profile in await db.Profiles.ListAsync())
        {
            total += (await db.Favorites.ListAsync(new ProfileScope(profile.Id))).Count;
        }

        return total;
    }
}
