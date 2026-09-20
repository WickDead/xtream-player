namespace XtreamPlayer.Core.Library;

/// <summary>
/// When playback progress is worth recording.
/// </summary>
/// <remarks>
/// In Core rather than beside the player, because it is policy rather than UI
/// and because it crashed the app. The player tracked the last checkpoint as
/// <see cref="TimeSpan.MinValue"/> until one had been written, and subtracting
/// that from any real position overflows — so the first checkpoint threw, out
/// through the navigation call that writes one on the way out, and the process
/// died whenever the player was left after watching anything. Arithmetic that
/// can do that belongs somewhere a test can reach it.
/// </remarks>
public static class ResumePolicy
{
    /// <summary>
    /// Below this, an accidental open should not enter Continue Watching.
    /// </summary>
    public static readonly TimeSpan MinimumWatched = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Progress smaller than this is not worth a database write; it is what
    /// stops a paused player checkpointing on every tick.
    /// </summary>
    public static readonly TimeSpan MinimumProgress = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether the current position is worth writing a resume point for.
    /// </summary>
    /// <param name="position">Where playback has reached.</param>
    /// <param name="duration">Total length; zero or less for a live stream.</param>
    /// <param name="last">The previous checkpoint, or null if none was written.</param>
    public static bool ShouldCheckpoint(TimeSpan position, TimeSpan duration, TimeSpan? last)
    {
        // Nothing to resume into, or too little watched to be worth remembering.
        if (duration <= TimeSpan.Zero || position < MinimumWatched)
        {
            return false;
        }

        // Null means nothing has been written yet, and the position already
        // cleared the minimum above.
        if (last is not { } previous)
        {
            return true;
        }

        // Differenced in double rather than as TimeSpans. Subtracting one
        // TimeSpan from another overflows near the extremes, which is the bug
        // that took the app down, and a function that decides whether to write a
        // row should not be able to throw for any input a caller can hand it.
        //
        // Precision only matters close to the threshold, where the values are
        // small and exact; far from it the answer is not in doubt.
        var moved = Math.Abs((double)position.Ticks - previous.Ticks);

        // Absolute, so seeking backwards counts as movement too.
        return moved >= MinimumProgress.Ticks;
    }
}
