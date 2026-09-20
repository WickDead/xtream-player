using Xunit;
using XtreamPlayer.Core.Library;

namespace XtreamPlayer.Tests;

/// <summary>
/// When playback progress is worth recording.
/// </summary>
/// <remarks>
/// This exists because the decision crashed the app. The player held the last
/// checkpoint as <see cref="TimeSpan.MinValue"/> until one had been written, and
/// subtracting that from a real position overflows — so the first checkpoint
/// threw, out through the navigation call that writes one on the way out, and
/// leaving the player after watching anything killed the process.
/// </remarks>
public sealed class ResumePolicyTests
{
    private static readonly TimeSpan Film = TimeSpan.FromHours(1.5);

    [Fact]
    public void WritesTheFirstCheckpointOnceEnoughHasBeenWatched()
    {
        Assert.True(ResumePolicy.ShouldCheckpoint(TimeSpan.FromMinutes(5), Film, last: null));
    }

    [Fact]
    public void NeverOverflowsWhateverThePreviousCheckpointWas()
    {
        // The regression itself. Any of these subtracted from a real position
        // must not throw, whatever the arithmetic underneath ends up being.
        TimeSpan?[] awkward =
        [
            null,
            TimeSpan.MinValue,
            TimeSpan.MaxValue,
            TimeSpan.Zero,
            TimeSpan.FromDays(-1),
        ];

        foreach (var last in awkward)
        {
            var exception = Record.Exception(
                () => ResumePolicy.ShouldCheckpoint(TimeSpan.FromMinutes(5), Film, last));

            Assert.Null(exception);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(29)]
    public void IgnoresAnAccidentalOpen(int seconds)
    {
        // Opening something and immediately leaving must not put it in Continue
        // Watching.
        Assert.False(ResumePolicy.ShouldCheckpoint(
            TimeSpan.FromSeconds(seconds), Film, last: null));
    }

    [Fact]
    public void RecordsExactlyAtTheThreshold()
    {
        Assert.True(ResumePolicy.ShouldCheckpoint(ResumePolicy.MinimumWatched, Film, last: null));
    }

    [Fact]
    public void SkipsAWriteWhileNothingIsMoving()
    {
        // A paused player keeps ticking; without this it would write a row every
        // time.
        var position = TimeSpan.FromMinutes(20);

        Assert.False(ResumePolicy.ShouldCheckpoint(
            position, Film, position - TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void WritesOnceProgressPassesTheThreshold()
    {
        var position = TimeSpan.FromMinutes(20);

        Assert.True(ResumePolicy.ShouldCheckpoint(
            position, Film, position - ResumePolicy.MinimumProgress));
    }

    [Fact]
    public void CountsSeekingBackwardsAsMovement()
    {
        // Jumping back is progress worth recording; comparing signed values
        // would treat it as nothing having happened.
        var position = TimeSpan.FromMinutes(20);

        Assert.True(ResumePolicy.ShouldCheckpoint(
            position, Film, position + TimeSpan.FromMinutes(4)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RecordsNothingForALiveStream(int durationSeconds)
    {
        // Live reports no duration, and there is nothing to resume into.
        Assert.False(ResumePolicy.ShouldCheckpoint(
            TimeSpan.FromMinutes(40), TimeSpan.FromSeconds(durationSeconds), last: null));
    }
}
