using Xunit;
using XtreamPlayer.Core.Library;
using XtreamPlayer.Core.Models;

namespace XtreamPlayer.Tests;

/// <summary>
/// Overlap resolution for the guide grid.
/// </summary>
/// <remarks>
/// The shapes here are taken from the live provider's feed, where 1,643 of
/// 21,693 programmes overlapped their successor across 131 channels.
/// </remarks>
public sealed class EpgTimelineTests
{
    private static readonly DateTimeOffset Day =
        new(2026, 9, 19, 0, 0, 0, TimeSpan.Zero);

    private static EpgProgramme At(string title, double startHour, double endHour) => new()
    {
        XmltvChannelId = "ch",
        Title = title,
        Start = Day.AddHours(startHour),
        Stop = Day.AddHours(endHour),
    };

    [Fact]
    public void LeavesCleanListingsExactlyAsTheyWere()
    {
        List<EpgProgramme> input = [At("News", 8, 9), At("Film", 9, 11), At("Talk", 11, 11.5)];

        var result = EpgTimeline.Normalize(input);

        Assert.Equal(3, result.Count);
        // Reference equality: a clean row must not be rebuilt at all.
        Assert.True(result.Zip(input).All(pair => ReferenceEquals(pair.First, pair.Second)));
    }

    [Fact]
    public void ReturnsListingsThatNeverCoverTheSameInstantTwice()
    {
        // FoxBusiness.us: a twelve-hour block with a half-hour one inside it.
        List<EpgProgramme> input = [At("Cops", 8.5, 20.5), At("Cops", 9, 9.5)];

        var result = EpgTimeline.Normalize(input);

        AssertDisjointAndSorted(result);
    }

    [Fact]
    public void PrefersTheMoreSpecificListingWhereTwoCoverTheSameTime()
    {
        // beINSports9.qa: a thirteen-hour umbrella event with a real competition
        // inside it. The competition is the useful listing.
        List<EpgProgramme> input =
        [
            At("Longines Global Champions Tour", 0, 13),
            At("GCL R2 - Competition Team", 1.5, 2.5),
        ];

        var result = EpgTimeline.Normalize(input);

        AssertDisjointAndSorted(result);
        Assert.Equal(3, result.Count);

        Assert.Equal("Longines Global Champions Tour", result[0].Title);
        Assert.Equal(Day, result[0].Start);
        Assert.Equal(Day.AddHours(1.5), result[0].Stop);

        Assert.Equal("GCL R2 - Competition Team", result[1].Title);
        Assert.Equal(Day.AddHours(1.5), result[1].Start);
        Assert.Equal(Day.AddHours(2.5), result[1].Stop);

        Assert.Equal("Longines Global Champions Tour", result[2].Title);
        Assert.Equal(Day.AddHours(2.5), result[2].Start);
        Assert.Equal(Day.AddHours(13), result[2].Stop);
    }

    [Fact]
    public void RejoinsOneListingSplitByADuplicateOfItself()
    {
        // The reason this matters: without rejoining, the twelve-hour "Cops"
        // block is drawn as three consecutive blocks all captioned "Cops",
        // which reads as a new bug rather than a fix for the old one.
        List<EpgProgramme> input = [At("Cops", 8.5, 20.5), At("Cops", 9, 9.5)];

        var result = EpgTimeline.Normalize(input);

        var single = Assert.Single(result);
        Assert.Equal("Cops", single.Title);
        Assert.Equal(Day.AddHours(8.5), single.Start);
        Assert.Equal(Day.AddHours(20.5), single.Stop);
    }

    [Fact]
    public void RejoinsDuplicatesThatMerelyStartAtDifferentTimes()
    {
        // RacingCom.au: the same programme listed twice with staggered starts
        // and a shared, implausible stop.
        List<EpgProgramme> input = [At("Racing Live", 1.25, 8), At("Racing Live", 1.85, 8)];

        var result = EpgTimeline.Normalize(input);

        var single = Assert.Single(result);
        Assert.Equal(Day.AddHours(1.25), single.Start);
        Assert.Equal(Day.AddHours(8), single.Stop);
    }

    [Fact]
    public void ClipsAListingThatRunsPastTheNextOnesStart()
    {
        List<EpgProgramme> input = [At("Match", 18, 20.5), At("News", 20, 21)];

        var result = EpgTimeline.Normalize(input);

        AssertDisjointAndSorted(result);
        Assert.Equal(2, result.Count);
        Assert.Equal(Day.AddHours(20), result[0].Stop);
        Assert.Equal(Day.AddHours(20), result[1].Start);
        Assert.Equal("News", result[1].Title);
    }

    [Fact]
    public void DropsTheSliverLeftWhenAnOverlayStartsAlmostImmediately()
    {
        // MATCH3.HOME.MLB: a five-hour "LIVE" block with a "NEXT" marker
        // starting two minutes in. The two-minute head of LIVE is an artifact.
        var live = new EpgProgramme
        {
            XmltvChannelId = "ch",
            Title = "LIVE - Brewers at Orioles",
            Start = Day.AddHours(23.0833),
            Stop = Day.AddHours(28.0833),
        };

        var next = new EpgProgramme
        {
            XmltvChannelId = "ch",
            Title = "NEXT - Brewers at Orioles",
            Start = Day.AddHours(23.1166),
            Stop = Day.AddHours(23.6166),
        };

        var result = EpgTimeline.Normalize([live, next]);

        AssertDisjointAndSorted(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("NEXT - Brewers at Orioles", result[0].Title);
        Assert.Equal("LIVE - Brewers at Orioles", result[1].Title);
    }

    [Fact]
    public void KeepsAGenuinelyShortProgrammeThatWasNeverClipped()
    {
        // The minimum-length rule applies only to remnants. A real two-minute
        // interstitial on an otherwise messy row must survive.
        List<EpgProgramme> input =
        [
            At("Feature", 10, 12),
            At("Ident", 12, 12.0333),
            At("Overlapping", 11, 13),
        ];

        var result = EpgTimeline.Normalize(input);

        AssertDisjointAndSorted(result);
        Assert.Contains(result, p => p.Title == "Ident");
    }

    [Fact]
    public void KeepsBackToBackEpisodesThatShareATitle()
    {
        // FoxBusiness.us runs twenty consecutive half-hour episodes of "Cops"
        // and also carries an overlap elsewhere on the row, so the whole channel
        // takes the resolving path. An earlier rule joined adjacent slices by
        // title alone and collapsed all twenty into one ten-hour block, taking
        // the channel from fifty listings to fourteen.
        var input = new List<EpgProgramme>();

        for (var i = 0; i < 20; i++)
        {
            input.Add(At("Cops", i * 0.5, (i + 1) * 0.5));
        }

        // The overlap that forces the slow path.
        input.Add(At("Live: Kudlow", 10.5, 12));
        input.Add(At("Live: The Evening Edit", 11, 12.5));

        var result = EpgTimeline.Normalize(input);

        AssertDisjointAndSorted(result);
        Assert.Equal(20, result.Count(p => p.Title == "Cops"));
    }

    [Fact]
    public void DropsListingsWithNoDuration()
    {
        List<EpgProgramme> input = [At("Zero", 9, 9), At("Real", 8, 10)];

        var result = EpgTimeline.Normalize(input);

        var single = Assert.Single(result);
        Assert.Equal("Real", single.Title);
    }

    [Fact]
    public void LeavesAGapWhereTheFeedLeftOne()
    {
        List<EpgProgramme> input = [At("Early", 8, 9), At("Late", 11, 12), At("Spanning", 8.5, 9.5)];

        var result = EpgTimeline.Normalize(input);

        AssertDisjointAndSorted(result);

        // Nothing may be invented across 09:30 to 11:00.
        Assert.DoesNotContain(result, p =>
            p.Start < Day.AddHours(11) && p.Stop > Day.AddHours(9.5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void HandlesTrivialInputs(int count)
    {
        var input = Enumerable.Range(0, count).Select(i => At($"P{i}", i, i + 1)).ToList();

        Assert.Equal(count, EpgTimeline.Normalize(input).Count);
    }

    private static void AssertDisjointAndSorted(IReadOnlyList<EpgProgramme> programmes)
    {
        for (var i = 0; i < programmes.Count; i++)
        {
            Assert.True(programmes[i].Stop > programmes[i].Start,
                $"Programme {i} '{programmes[i].Title}' has no duration.");

            if (i > 0)
            {
                Assert.True(programmes[i].Start >= programmes[i - 1].Stop,
                    $"'{programmes[i].Title}' starts before '{programmes[i - 1].Title}' ends.");
            }
        }
    }
}
