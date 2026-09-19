using XtreamPlayer.Core.Library;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Infrastructure.Persistence;
using XtreamPlayer.Infrastructure.Security;
using Xunit;

namespace XtreamPlayer.Tests;

/// <summary>
/// A throwaway on-disk database per test. Not in-memory: these exercise the
/// real connection factory, which migrates on first open.
/// </summary>
public sealed class TempDatabase : IDisposable
{
    private readonly string _directory;

    public TempDatabase()
    {
        _directory = Path.Combine(Path.GetTempPath(), "xp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        Connections = new SqliteConnectionFactory(
            Path.Combine(_directory, DatabasePaths.DatabaseFileName));

        // Forced plaintext so assertions do not depend on DPAPI availability.
        Protector = new CredentialProtector(PasswordProtection.Plaintext);

        Profiles = new ProfileRepository(Connections, Protector);
        Favorites = new FavoritesRepository(Connections);
        Resume = new ResumeRepository(Connections);
        Hidden = new HiddenCategoryRepository(Connections);
        Settings = new SettingsRepository(Connections);
    }

    public SqliteConnectionFactory Connections { get; }
    public CredentialProtector Protector { get; }
    public ProfileRepository Profiles { get; }
    public FavoritesRepository Favorites { get; }
    public ResumeRepository Resume { get; }
    public HiddenCategoryRepository Hidden { get; }
    public SettingsRepository Settings { get; }

    public async Task<Profile> AddProfileAsync(string name = "Test", string password = "pw") =>
        await Profiles.SaveAsync(new Profile
        {
            Id = string.Empty,
            Name = name,
            ServerUrl = "example.pw",
            Username = "user",
            Password = password,
        });

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A lingering WAL handle on Windows; the temp directory is disposable.
        }
    }
}

public sealed class ProfileRepositoryTests
{
    [Theory]
    // Ports XtreamClient::new normalization from the old Rust client.
    [InlineData("example.pw", "http://example.pw")]
    [InlineData("http://example.pw/", "http://example.pw")]
    [InlineData("http://example.pw///", "http://example.pw")]
    [InlineData("https://example.pw:8080/", "https://example.pw:8080")]
    [InlineData("  example.pw  ", "http://example.pw")]
    public void NormalizesServerUrl(string input, string expected) =>
        Assert.Equal(expected, ServerUrl.Normalize(input));

    [Fact]
    public async Task SavesAndRoundTripsAProfile()
    {
        using var db = new TempDatabase();

        var saved = await db.AddProfileAsync("Wicked", "Sekr3tPass");

        Assert.NotEmpty(saved.Id);
        Assert.Equal("http://example.pw", saved.ServerUrl);

        var loaded = await db.Profiles.GetAsync(new ProfileScope(saved.Id));

        Assert.NotNull(loaded);
        Assert.Equal("Wicked", loaded.Name);
        Assert.Equal("Sekr3tPass", loaded.Password);
        Assert.False(loaded.PasswordUnavailable);
    }

    [Fact]
    public async Task UpdatingOneProfileLeavesOthersAlone()
    {
        using var db = new TempDatabase();
        var first = await db.AddProfileAsync("First");
        var second = await db.AddProfileAsync("Second");

        await db.Profiles.SaveAsync(second with { Name = "Renamed" });

        var reloaded = await db.Profiles.ListAsync();

        Assert.Equal(2, reloaded.Count);
        Assert.Equal("First", reloaded.Single(p => p.Id == first.Id).Name);
        Assert.Equal("Renamed", reloaded.Single(p => p.Id == second.Id).Name);
    }

    [Fact]
    public async Task UpdateKeepsOriginalCreatedAt()
    {
        using var db = new TempDatabase();
        var saved = await db.AddProfileAsync();

        var updated = await db.Profiles.SaveAsync(saved with { Name = "Changed" });

        Assert.Equal(saved.CreatedAt, updated.CreatedAt);
        Assert.True(updated.UpdatedAt >= saved.UpdatedAt);
    }

    [Fact]
    public void ProfileScopeRejectsEmptyIds()
    {
        Assert.Throws<ArgumentException>(() => new ProfileScope(""));
        Assert.Throws<ArgumentException>(() => new ProfileScope("   "));
    }
}

public sealed class FavoritesRepositoryTests
{
    /// <summary>
    /// The bug that made favorites leak between providers: the old queries had
    /// no WHERE profile_id at all.
    /// </summary>
    [Fact]
    public async Task FavoritesAreScopedToTheirProfile()
    {
        using var db = new TempDatabase();
        var a = new ProfileScope((await db.AddProfileAsync("A")).Id);
        var b = new ProfileScope((await db.AddProfileAsync("B")).Id);

        await db.Favorites.AddAsync(a, "604", ContentKind.Live, "Sportsnet");
        await db.Favorites.AddAsync(b, "604", ContentKind.Live, "Totally Different Channel");

        var forA = await db.Favorites.ListAsync(a);
        var forB = await db.Favorites.ListAsync(b);

        Assert.Single(forA);
        Assert.Single(forB);
        Assert.Equal("Sportsnet", forA[0].Title);
        Assert.Equal("Totally Different Channel", forB[0].Title);
    }

    /// <summary>
    /// Xtream live and VOD stream ids share one numeric namespace. The old
    /// dedupe ignored kind, so favoriting live 1234 blocked favoriting movie 1234.
    /// </summary>
    [Fact]
    public async Task SameContentIdInDifferentKindsCoexist()
    {
        using var db = new TempDatabase();
        var scope = new ProfileScope((await db.AddProfileAsync()).Id);

        await db.Favorites.AddAsync(scope, "1234", ContentKind.Live, "Channel 1234");
        await db.Favorites.AddAsync(scope, "1234", ContentKind.Vod, "Movie 1234");

        var all = await db.Favorites.ListAsync(scope);

        Assert.Equal(2, all.Count);
        Assert.Contains(all, f => f.Kind == ContentKind.Live);
        Assert.Contains(all, f => f.Kind == ContentKind.Vod);
    }

    [Fact]
    public async Task AddingTwiceReturnsTheExistingRow()
    {
        using var db = new TempDatabase();
        var scope = new ProfileScope((await db.AddProfileAsync()).Id);

        var first = await db.Favorites.AddAsync(scope, "604", ContentKind.Live, "Sportsnet");
        var second = await db.Favorites.AddAsync(scope, "604", ContentKind.Live, "Sportsnet");

        Assert.Equal(first.Id, second.Id);
        Assert.Single(await db.Favorites.ListAsync(scope));
    }

    [Fact]
    public async Task RemoveByIdWillNotReachAcrossProfiles()
    {
        using var db = new TempDatabase();
        var a = new ProfileScope((await db.AddProfileAsync("A")).Id);
        var b = new ProfileScope((await db.AddProfileAsync("B")).Id);

        var favorite = await db.Favorites.AddAsync(a, "604", ContentKind.Live, "Sportsnet");

        // Profile B trying to delete profile A's row must do nothing.
        await db.Favorites.RemoveByIdAsync(b, favorite.Id);

        Assert.Single(await db.Favorites.ListAsync(a));
    }
}

public sealed class ResumeRepositoryTests
{
    [Fact]
    public async Task ComputesProgressPercentFromPosition()
    {
        using var db = new TempDatabase();
        var profile = await db.AddProfileAsync();

        await db.Resume.SaveAsync(new ResumeItem
        {
            Id = string.Empty,
            ProfileId = profile.Id,
            ContentId = "1359763",
            Kind = ContentKind.Vod,
            Title = "Lone Hero",
            Position = TimeSpan.FromMinutes(30),
            Duration = TimeSpan.FromMinutes(100),
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var item = await db.Resume.GetAsync(
            new ProfileScope(profile.Id), "1359763", ContentKind.Vod);

        Assert.NotNull(item);
        Assert.Equal(30, item.ProgressPercent);
        Assert.Equal(TimeSpan.FromMinutes(30), item.Position);
    }

    [Fact]
    public async Task ContinueWatchingExcludesFinishedAndLive()
    {
        using var db = new TempDatabase();
        var profile = await db.AddProfileAsync();
        var scope = new ProfileScope(profile.Id);

        await db.Resume.SaveAsync(Item(profile.Id, "1", ContentKind.Vod, finished: false));
        await db.Resume.SaveAsync(Item(profile.Id, "2", ContentKind.Vod, finished: true));
        await db.Resume.SaveAsync(Item(profile.Id, "3", ContentKind.Live, finished: false));

        var watching = await db.Resume.ListContinueWatchingAsync(scope);

        Assert.Single(watching);
        Assert.Equal("1", watching[0].ContentId);
    }

    [Fact]
    public async Task SavingTheSameItemTwiceUpdatesRatherThanDuplicates()
    {
        using var db = new TempDatabase();
        var profile = await db.AddProfileAsync();
        var scope = new ProfileScope(profile.Id);

        await db.Resume.SaveAsync(Item(profile.Id, "1", ContentKind.Vod, finished: false));
        await db.Resume.SaveAsync(Item(profile.Id, "1", ContentKind.Vod, finished: false,
            position: TimeSpan.FromMinutes(42)));

        var watching = await db.Resume.ListContinueWatchingAsync(scope);

        Assert.Single(watching);
        Assert.Equal(TimeSpan.FromMinutes(42), watching[0].Position);
    }

    [Fact]
    public async Task RejectsAnUnscopedWrite()
    {
        using var db = new TempDatabase();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            db.Resume.SaveAsync(Item(string.Empty, "1", ContentKind.Vod, finished: false)));
    }

    private static ResumeItem Item(
        string profileId, string contentId, ContentKind kind, bool finished,
        TimeSpan? position = null) => new()
    {
        Id = string.Empty,
        ProfileId = profileId,
        ContentId = contentId,
        Kind = kind,
        Title = contentId,
        Position = position ?? TimeSpan.FromMinutes(10),
        Duration = TimeSpan.FromMinutes(100),
        Finished = finished,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}

public sealed class HiddenCategoryTests
{
    [Fact]
    public async Task HidingIsScopedToProfileAndCatalog()
    {
        using var db = new TempDatabase();
        var a = new ProfileScope((await db.AddProfileAsync("A")).Id);
        var b = new ProfileScope((await db.AddProfileAsync("B")).Id);

        await db.Hidden.HideAsync(a, CategoryScope.Live, "c1", "Sports");

        Assert.Single(await db.Hidden.ListAsync(a, CategoryScope.Live));
        // Not leaked to the other provider, nor to another catalog.
        Assert.Empty(await db.Hidden.ListAsync(b, CategoryScope.Live));
        Assert.Empty(await db.Hidden.ListAsync(a, CategoryScope.Vod));
    }

    [Fact]
    public void ResolverMatchesOnIdFirst()
    {
        var resolver = new HiddenCategoryResolver(
        [
            Hidden("c1", "Sports"),
        ]);

        Assert.True(resolver.IsHidden("c1", "Sports"));
        Assert.False(resolver.IsHidden("c2", "Movies"));
        Assert.Empty(resolver.Repoints);
    }

    /// <summary>
    /// The failure mode the old localStorage version had: a provider renumbering
    /// its categories silently unhid everything.
    /// </summary>
    [Fact]
    public void ResolverSurvivesProviderRenumberingAndSelfHeals()
    {
        var resolver = new HiddenCategoryResolver(
        [
            Hidden("old-id", "Sports CA"),
        ]);

        // Same category, new id after a provider refresh.
        Assert.True(resolver.IsHidden("new-id", "Sports CA"));

        var repoint = Assert.Single(resolver.Repoints);
        Assert.Equal("old-id", repoint.OldCategoryId);
        Assert.Equal("new-id", repoint.NewCategoryId);
    }

    [Theory]
    [InlineData("Sports CA", "sports ca")]
    [InlineData("Sports CA", "SPORTS-CA")]
    [InlineData("Sports CA", "Sports  CA!")]
    public void ResolverNameMatchIgnoresCaseAndPunctuation(string stored, string incoming)
    {
        var resolver = new HiddenCategoryResolver([Hidden("old", stored)]);

        Assert.True(resolver.IsHidden("new", incoming));
    }

    [Fact]
    public void ResolverDoesNotMatchUnrelatedNames()
    {
        var resolver = new HiddenCategoryResolver([Hidden("old", "Sports CA")]);

        Assert.False(resolver.IsHidden("new", "Kids"));
    }

    private static HiddenCategory Hidden(string id, string name) => new()
    {
        ProfileId = "p",
        Scope = CategoryScope.Live,
        CategoryId = id,
        CategoryName = name,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}

public sealed class SettingsRepositoryTests
{
    [Fact]
    public async Task StoresReadsAndOverwrites()
    {
        using var db = new TempDatabase();

        Assert.Null(await db.Settings.GetAsync("missing"));

        await db.Settings.SetAsync("k", "one");
        Assert.Equal("one", await db.Settings.GetAsync("k"));

        await db.Settings.SetAsync("k", "two");
        Assert.Equal("two", await db.Settings.GetAsync("k"));

        await db.Settings.RemoveAsync("k");
        Assert.Null(await db.Settings.GetAsync("k"));
    }
}
