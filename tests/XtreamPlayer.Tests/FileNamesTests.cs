using Xunit;
using XtreamPlayer.Core.Library;

namespace XtreamPlayer.Tests;

/// <summary>
/// Turning provider titles into paths Windows will accept.
/// </summary>
/// <remarks>
/// Provider titles carry colons, slashes, quotes and emoji, any of which makes
/// a path invalid. The cases that actually bite are the quiet ones: Windows
/// strips a trailing dot without complaint, so "Movie." is created as "Movie"
/// and every later lookup by the original name misses.
/// </remarks>
public sealed class FileNamesTests
{
    [Theory]
    [InlineData("The Matrix", "The Matrix")]
    [InlineData("Alien: Covenant", "Alien Covenant")]
    [InlineData("Face/Off", "Face Off")]
    [InlineData("What's Up?", "What's Up")]
    [InlineData("A*B|C<D>E", "A B C D E")]
    public void StripsCharactersAPathCannotContain(string input, string expected)
    {
        var result = FileNames.Sanitize(input);

        Assert.Equal(expected, result);
        Assert.DoesNotContain(result, c => Path.GetInvalidFileNameChars().Contains(c));
    }

    [Fact]
    public void CollapsesRunsOfStrippedCharactersRatherThanLeavingATrail()
    {
        // "Title :: Subtitle" should not become "Title    Subtitle".
        Assert.Equal("Title Subtitle", FileNames.Sanitize("Title :: Subtitle"));
    }

    [Theory]
    [InlineData("Movie.")]
    [InlineData("Movie ")]
    [InlineData("Movie. . ")]
    public void RemovesTrailingDotsAndSpacesWindowsWouldSilentlyStrip(string input)
    {
        var result = FileNames.Sanitize(input);

        Assert.Equal("Movie", result);
        Assert.DoesNotMatch(@"[. ]$", result);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("PRN")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("NUL")]
    public void EscapesWindowsDeviceNames(string reserved)
    {
        // A file called CON.mkv cannot be created at all, in any directory.
        Assert.NotEqual(reserved, FileNames.Sanitize(reserved), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void EscapesAReservedNameEvenWithAnExtension()
    {
        Assert.NotEqual("CON.mkv", FileNames.Sanitize("CON.mkv"), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void FallsBackWhenNothingUsableSurvives()
    {
        Assert.Equal("download", FileNames.Sanitize("///"));
        Assert.Equal("download", FileNames.Sanitize("   "));
        Assert.Equal("download", FileNames.Sanitize(null));
        Assert.Equal("movie", FileNames.Sanitize("", "movie"));
    }

    [Fact]
    public void CapsLengthSoTheFullPathStaysUnderTheLimit()
    {
        var result = FileNames.Sanitize(new string('a', 400));

        Assert.True(result.Length <= 120, $"Got {result.Length} characters.");
    }

    [Fact]
    public void BuildsAnEpisodePathThatSortsAndScrapesCorrectly()
    {
        var path = FileNames.EpisodePath(@"C:\Videos", "The Wire", 1, 2, "The Detail", "mkv");

        Assert.Equal(
            Path.Combine(@"C:\Videos", "Series", "The Wire", "Season 01",
                "The Wire - S01E02 - The Detail.mkv"),
            path);
    }

    [Fact]
    public void ZeroPadsSeasonAndEpisodeNumbers()
    {
        var path = FileNames.EpisodePath(@"C:\Videos", "Show", 3, 7, "Title", "mp4");

        // Padding is what makes an episode list sort correctly, and what media
        // scrapers match against.
        Assert.Contains("S03E07", path, StringComparison.Ordinal);
        Assert.Contains("Season 03", path, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalisesTheExtensionWhetherOrNotItCarriesADot()
    {
        Assert.EndsWith(".mkv", FileNames.MoviePath(@"C:\V", "Film", "mkv"), StringComparison.Ordinal);
        Assert.EndsWith(".mkv", FileNames.MoviePath(@"C:\V", "Film", ".mkv"), StringComparison.Ordinal);
    }

    [Fact]
    public void SanitisesTheSeriesNameInBothTheFolderAndTheFile()
    {
        var path = FileNames.EpisodePath(@"C:\V", "Marvel: Daredevil", 1, 1, "Into the Ring", "mkv");

        Assert.DoesNotContain(":", path[3..], StringComparison.Ordinal);
    }

    [Fact]
    public void ReturnsThePlainPathWhenNothingIsInTheWay()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            Assert.Equal(
                Path.Combine(directory, "Film.mkv"),
                FileNames.Unique(directory, "Film.mkv"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AvoidsOverwritingAnExistingFileOrAPartialDownload()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(Path.Combine(directory, "Film.mkv"), "first");

            var second = FileNames.Unique(directory, "Film.mkv");
            Assert.Equal(Path.Combine(directory, "Film (2).mkv"), second);

            // A .part in the way counts too: that download is still running, and
            // a second job writing the same partial would corrupt both.
            File.WriteAllText(Path.Combine(directory, "Film (2).mkv.part"), "partial");

            Assert.Equal(Path.Combine(directory, "Film (3).mkv"),
                FileNames.Unique(directory, "Film.mkv"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
