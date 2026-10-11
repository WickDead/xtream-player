using XtreamPlayer.Core.Library;
using XtreamPlayer.Core.Models;
using Xunit;

namespace XtreamPlayer.Tests;

public sealed class NewEpisodeMatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);

    private static SeriesItem Series(string id, string name, DateTimeOffset? modified = null, int? year = null) => new()
    {
        Id = id,
        Name = name,
        CategoryId = "c",
        Year = year,
        LastModified = modified,
    };

    private static AiredEpisode Aired(
        string show, double daysAgo, int showId = 1, int? premiered = null, int episodeId = 0,
        int? season = 1, int? number = 1) =>
        new(episodeId == 0 ? (int)(daysAgo * 100) + showId * 10_000 : episodeId,
            showId, show, premiered, season, number, "Pilot", Now - TimeSpan.FromDays(daysAgo));

    private static IReadOnlyDictionary<string, IReadOnlyList<AiredEpisode>> Match(
        SeriesItem[] series, params AiredEpisode[] aired) =>
        NewEpisodeMatcher.FindCandidates(series, aired, Now, Week);

    private static ProviderEpisode Has(int season, int number, double addedDaysAgo) =>
        new(season, number, Now - TimeSpan.FromDays(addedDaysAgo));

    [Theory]
    [InlineData("Doc (2025)", "doc", 2025)]
    [InlineData("The Final Problem - 2026", "thefinalproblem", 2026)]
    [InlineData("Grey's Anatomy", "greysanatomy", null)]
    [InlineData("Law & Order: Special Victims Unit", "lawandorderspecialvictimsunit", null)]
    [InlineData("EN - Severance", "severance", null)]
    [InlineData("UK | Taskmaster", "taskmaster", null)]
    // Real titles that look like decoration and must be left alone.
    [InlineData("CSI: Vegas", "csivegas", null)]
    [InlineData("FBI: Most Wanted", "fbimostwanted", null)]
    [InlineData("1923", "1923", null)]
    [InlineData("Beverly Hills, 90210", "beverlyhills90210", null)]
    public void NormalizesProviderTitles(string title, string key, int? year) =>
        Assert.Equal((key, year), NewEpisodeMatcher.NormalizeTitle(title));

    [Fact]
    public void MatchesAcrossProviderDecoration()
    {
        var result = Match(
            [Series("a", "Doc (2025)", Now), Series("b", "Law & Order", Now)],
            Aired("Doc", 2, showId: 1, premiered: 2025),
            Aired("Law and Order", 3, showId: 2, premiered: 2022));

        Assert.Equal(["a", "b"], result.Keys.Order());
    }

    /// <summary>A remake shares the title, not the show.</summary>
    [Fact]
    public void AYearMismatchRejectsTheMatch()
    {
        var aired = Aired("Matlock", 1, premiered: 2024);

        Assert.Empty(Match([Series("old", "Matlock (1986)", Now)], aired));
        Assert.Single(Match([Series("new", "Matlock (2024)", Now)], aired));
        Assert.Single(Match([Series("new", "Matlock", Now)], aired));

        // Off by one is tolerated: release dates and premieres disagree.
        Assert.Single(Match([Series("new", "Matlock (2025)", Now)], aired));
    }

    /// <summary>
    /// The provider's release year often tracks the latest season, so only a
    /// year written into the title is used to reject.
    /// </summary>
    [Fact]
    public void TheProvidersReleaseYearIsNotTrusted() =>
        Assert.Single(Match([Series("a", "Shark Tank", Now, year: 2024)], Aired("Shark Tank", 1, premiered: 2009)));

    [Fact]
    public void DuplicateProviderEntriesAllQualify()
    {
        var result = Match(
            [Series("1", "Survivor", Now), Series("2", "Survivor (2000)", Now), Series("3", "Australian Survivor", Now)],
            Aired("Survivor", 2, premiered: 2000));

        Assert.Equal(["1", "2"], result.Keys.Order());
    }

    /// <summary>
    /// The catalog's stamp moves for reasons other than new episodes, so it
    /// decides nothing; the episode list does.
    /// </summary>
    [Fact]
    public void CandidatesIgnoreTheCatalogStamp()
    {
        var aired = Aired("Abbott Elementary", 3);

        Assert.Single(Match([Series("a", "Abbott Elementary", Now - TimeSpan.FromDays(30))], aired));
        Assert.Single(Match([Series("a", "Abbott Elementary", null)], aired));
    }

    [Fact]
    public void OnlyEpisodesInsideTheWindowCount()
    {
        var series = new[] { Series("a", "Severance", Now) };

        Assert.Empty(Match(series, Aired("Severance", 8)));
        Assert.Empty(Match(series, Aired("Severance", -1)));
        Assert.Single(Match(series, Aired("Severance", 6.9)));
    }

    [Fact]
    public void CandidateEpisodesAreLatestFirst()
    {
        var result = Match(
            [Series("a", "The Simpsons", Now)],
            Aired("The Simpsons", 6, episodeId: 1),
            Aired("The Simpsons", 1, episodeId: 2),
            Aired("The Simpsons", 4, episodeId: 3));

        Assert.Equal([2, 3, 1], result["a"].Select(e => e.EpisodeId));
    }

    /// <summary>
    /// The case this check exists for: the provider touched Gold Rush the day
    /// S17E1 aired, but its newest episode was S14E1.
    /// </summary>
    [Fact]
    public void AnEpisodeTheProviderDoesNotHaveIsNotAvailable()
    {
        var aired = new[] { Aired("Gold Rush", 2, season: 17, number: 1) };

        Assert.Null(NewEpisodeMatcher.FindAvailable(aired, [Has(14, 1, 120), Has(13, 22, 400)]));
        Assert.NotNull(NewEpisodeMatcher.FindAvailable(aired, [Has(14, 1, 120), Has(17, 1, 1)]));
    }

    /// <summary>
    /// Matching numbering is not enough: an old catalog "Mickey Mouse
    /// Clubhouse" has an S2E7 from years ago.
    /// </summary>
    [Fact]
    public void AnOldCopyWithTheSameNumberingDoesNotCount()
    {
        var aired = new[] { Aired("Mickey Mouse Clubhouse", 3, season: 2, number: 7) };

        Assert.Null(NewEpisodeMatcher.FindAvailable(aired, [Has(2, 7, 2000)]));

        // Streaming drops land a little before the schedule's air time.
        Assert.NotNull(NewEpisodeMatcher.FindAvailable(aired, [Has(2, 7, 4)]));

        // A provider that does not say when it added an episode is taken at its word.
        Assert.NotNull(NewEpisodeMatcher.FindAvailable(aired, [new ProviderEpisode(2, 7, null)]));
    }

    /// <summary>
    /// Two episodes aired this week and only the earlier one is up yet: the
    /// series still qualifies, on that one.
    /// </summary>
    [Fact]
    public void AnEarlierEpisodeFromTheWeekStillQualifies()
    {
        var aired = new[]
        {
            Aired("Big Brother", 1, episodeId: 2, season: 28, number: 36),
            Aired("Big Brother", 3, episodeId: 1, season: 28, number: 35),
        };

        Assert.Equal(1, NewEpisodeMatcher.FindAvailable(aired, [Has(28, 35, 2)])?.EpisodeId);
        Assert.Equal(2, NewEpisodeMatcher.FindAvailable(aired, [Has(28, 35, 2), Has(28, 36, 0.5)])?.EpisodeId);
    }

    [Fact]
    public void AnEpisodeWithoutNumberingCannotBeConfirmed() =>
        Assert.Null(NewEpisodeMatcher.FindAvailable(
            [Aired("Daily Show", 1, season: null, number: null)], [Has(1, 1, 0.5)]));

    [Fact]
    public void EmptyInputsAreHandled()
    {
        Assert.Empty(Match([], Aired("Doc", 1)));
        Assert.Empty(Match([Series("a", "Doc", Now)]));
    }
}
