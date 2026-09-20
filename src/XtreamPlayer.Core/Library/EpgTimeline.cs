using XtreamPlayer.Core.Models;

namespace XtreamPlayer.Core.Library;

/// <summary>
/// Turns a channel's raw listings into a clean, non-overlapping timeline.
/// </summary>
/// <remarks>
/// <para>
/// XMLTV feeds routinely overlap. Measured against the live provider, 1,643 of
/// 21,693 programmes started before the previous one on their channel had
/// finished, across 131 channels. Two shapes account for all of it: a specific
/// listing sitting wholly inside an umbrella block (a one-hour competition
/// inside a thirteen-hour "Global Champions Tour"), and a block whose stop time
/// simply runs past the next one's start.
/// </para>
/// <para>
/// Drawn literally, those blocks stack and their titles render over each other.
/// It is also a correctness problem, not only a visual one: the grid binary-
/// searches each row for the programme running at a given time, and that search
/// assumes the listings are sorted and disjoint. With overlaps it can land on
/// the wrong entry, so "what's on now" is wrong too.
/// </para>
/// <para>
/// Where two listings cover the same instant, the <em>shorter</em> one wins. A
/// feed that declares a one-hour competition inside a thirteen-hour event means
/// the competition; the umbrella block is what it had to say before it knew
/// better. The umbrella still fills the time either side.
/// </para>
/// </remarks>
public static class EpgTimeline
{
    /// <summary>
    /// A leftover slice this short, where a longer listing was overpainted by a
    /// shorter one, is an artifact of the overlap rather than something anyone
    /// wants to read. Only ever applied to a slice that was clipped.
    /// </summary>
    private static readonly TimeSpan MinimumFragment = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Resolves overlaps, returning listings sorted by start and covering no
    /// instant twice.
    /// </summary>
    public static List<EpgProgramme> Normalize(IReadOnlyList<EpgProgramme> programmes)
    {
        ArgumentNullException.ThrowIfNull(programmes);

        // The overwhelming majority of channels are already clean, and leaving
        // them byte-identical confines all of this to the feeds that need it.
        return programmes.Count <= 1 || IsClean(programmes)
            ? [.. programmes]
            : Resolve(programmes);
    }

    /// <summary>Sorted, positive-length and disjoint — nothing to do.</summary>
    private static bool IsClean(IReadOnlyList<EpgProgramme> programmes)
    {
        for (var i = 0; i < programmes.Count; i++)
        {
            if (programmes[i].Stop <= programmes[i].Start)
            {
                return false;
            }

            // Sortedness follows: each start is at or after the previous stop,
            // which is itself after the previous start.
            if (i > 0 && programmes[i].Start < programmes[i - 1].Stop)
            {
                return false;
            }
        }

        return true;
    }

    private static List<EpgProgramme> Resolve(IReadOnlyList<EpgProgramme> programmes)
    {
        var usable = programmes.Where(p => p.Stop > p.Start).ToList();

        if (usable.Count == 0)
        {
            return [];
        }

        // Every instant at which the set of covering listings can change. Between
        // two adjacent edges the answer is constant, so each slice needs deciding
        // exactly once.
        var edges = new SortedSet<DateTimeOffset>();

        foreach (var programme in usable)
        {
            edges.Add(programme.Start);
            edges.Add(programme.Stop);
        }

        var points = edges.ToArray();
        var result = new List<EpgProgramme>(usable.Count);

        EpgProgramme? source = null;
        DateTimeOffset start = default;
        DateTimeOffset stop = default;

        for (var i = 0; i + 1 < points.Length; i++)
        {
            var from = points[i];
            var to = points[i + 1];
            var winner = MostSpecificCovering(usable, from, to);

            if (winner is null)
            {
                // A genuine gap in the listings.
                Flush(result, source, start, stop);
                source = null;
                continue;
            }

            if (source is not null && stop == from && ShouldJoin(source, start, stop, winner, from, to))
            {
                stop = to;
                continue;
            }

            Flush(result, source, start, stop);
            source = winner;
            start = from;
            stop = to;
        }

        Flush(result, source, start, stop);

        return result;
    }

    private static EpgProgramme? MostSpecificCovering(
        List<EpgProgramme> programmes, DateTimeOffset from, DateTimeOffset to)
    {
        EpgProgramme? winner = null;

        foreach (var programme in programmes)
        {
            if (programme.Start > from || programme.Stop < to)
            {
                continue;
            }

            if (winner is null || programme.Duration < winner.Duration)
            {
                winner = programme;
            }
        }

        return winner;
    }

    private static void Flush(
        List<EpgProgramme> result, EpgProgramme? source, DateTimeOffset start, DateTimeOffset stop)
    {
        if (source is null)
        {
            return;
        }

        var span = stop - start;

        // An untouched listing is kept whatever its length; only a clipped
        // remnant has to justify itself.
        var wasClipped = source.Start != start || source.Stop != stop;

        if (wasClipped && span < MinimumFragment)
        {
            return;
        }

        result.Add(wasClipped ? source with { Start = start, Stop = stop } : source);
    }

    /// <summary>
    /// Whether two adjacent slices are one programme that overlap resolution
    /// pulled apart, rather than two listings that merely share a name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matching on title alone is not enough, and getting this wrong is worse
    /// than the overlap it fixes. Fox Business lists twenty consecutive
    /// half-hour episodes of "Cops"; joining slices by title collapsed all
    /// twenty into a single ten-hour block and took the channel from fifty
    /// listings to fourteen.
    /// </para>
    /// <para>
    /// So at least one side must have been clipped. Two untouched listings sit
    /// exactly as the feed declared them and are left alone however alike they
    /// look, while a remnant either side of an overlay — which is the only thing
    /// this is here to repair — still joins back up.
    /// </para>
    /// </remarks>
    private static bool ShouldJoin(
        EpgProgramme run, DateTimeOffset runStart, DateTimeOffset runStop,
        EpgProgramme next, DateTimeOffset nextStart, DateTimeOffset nextStop)
    {
        if (!string.Equals(run.Title, next.Title, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var runIsWhole = run.Start == runStart && run.Stop == runStop;
        var nextIsWhole = next.Start == nextStart && next.Stop == nextStop;

        return !runIsWhole || !nextIsWhole;
    }
}
