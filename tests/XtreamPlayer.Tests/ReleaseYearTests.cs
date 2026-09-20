using Xunit;
using XtreamPlayer.Core.Library;

namespace XtreamPlayer.Tests;

/// <summary>
/// Pulling a year out of a provider's release-date field.
/// </summary>
/// <remarks>
/// The field is free text. The live provider sends <c>2015-01-15</c>, but the
/// same key elsewhere carries a bare year, a European date, a month name, or
/// <c>0000-00-00</c> from a column nobody filled in.
/// </remarks>
public sealed class ReleaseYearTests
{
    [Theory]
    [InlineData("2015-01-15", 2015)]
    [InlineData("2015", 2015)]
    [InlineData("15/01/2015", 2015)]
    [InlineData("01-15-2015", 2015)]
    [InlineData("January 2015", 2015)]
    [InlineData("2015-01-15 00:00:00", 2015)]
    [InlineData(" 1999 ", 1999)]
    public void FindsTheYearWhateverTheFormat(string input, int expected) =>
        Assert.Equal(expected, ReleaseYear.Parse(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0000-00-00")]
    [InlineData("N/A")]
    [InlineData("unknown")]
    public void ReturnsNothingWhenThereIsNoYear(string? input) =>
        Assert.Null(ReleaseYear.Parse(input));

    [Fact]
    public void RejectsAUnixTimestampRatherThanTakingItsFirstFourDigits()
    {
        // The neighbouring last_modified field is seconds since the epoch. Read
        // as a year it becomes 1789, which is plausible enough to render.
        Assert.Null(ReleaseYear.Parse("1789787764"));
    }

    [Fact]
    public void RejectsATmdbIdOfSimilarLength()
    {
        Assert.Null(ReleaseYear.Parse("619224"));
        Assert.Null(ReleaseYear.Parse("61922"));
    }

    [Fact]
    public void RejectsYearsOutsideThePlausibleRange()
    {
        Assert.Null(ReleaseYear.Parse("1234-01-01"));
        Assert.Null(ReleaseYear.Parse("0000"));
        Assert.Null(ReleaseYear.Parse("3999"));
    }

    [Fact]
    public void AcceptsTheEarliestFilmsAndNearFutureReleases()
    {
        Assert.Equal(1888, ReleaseYear.Parse("1888"));
        Assert.Equal(DateTime.UtcNow.Year + 1, ReleaseYear.Parse($"{DateTime.UtcNow.Year + 1}-06-01"));
    }

    [Fact]
    public void TakesTheFirstPlausibleYearWhenTheStringHasSeveral()
    {
        Assert.Equal(2015, ReleaseYear.Parse("2015-2018"));
    }

    [Fact]
    public void FormatReturnsNullRatherThanAnEmptyString()
    {
        // Bound directly to a TextBlock, so an empty string would leave a gap
        // where the visibility check expects nothing at all.
        Assert.Null(ReleaseYear.Format("rubbish"));
        Assert.Equal("2015", ReleaseYear.Format("2015-01-15"));
    }
}
