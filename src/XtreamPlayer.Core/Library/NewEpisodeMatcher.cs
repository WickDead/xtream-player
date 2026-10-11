using System.Text;
using System.Text.RegularExpressions;
using XtreamPlayer.Core.Models;

namespace XtreamPlayer.Core.Library;

/// <summary>
/// Finds the provider series that have an episode from the last week available.
/// </summary>
/// <remarks>
/// <para>
/// Two steps. <see cref="FindCandidates"/> pairs catalog series with schedule
/// shows by title, which needs nothing but the catalog. <see cref="FindAvailable"/>
/// then checks a candidate against the provider's own episode list, which costs
/// a request per series and is done by the caller.
/// </para>
/// <para>
/// The schedule and the catalog share nothing but a title. Provider titles carry
/// a disambiguating year ("Doc (2025)", "The Final Problem - 2026") and
/// occasionally a language tag ("EN - "); the schedule's names carry neither.
/// Remakes share titles, so a year in the provider's title has to agree with the
/// premiere.
/// </para>
/// <para>
/// The series' <c>last_modified</c> is not used: providers bump it for reasons
/// other than new episodes. Gold Rush was touched the day S17E1 aired while the
/// provider's newest episode was still S14E1.
/// </para>
/// </remarks>
public static partial class NewEpisodeMatcher
{
    /// <summary>
    /// How far before the air time a provider's copy may have been added.
    /// Streaming releases land a few hours early, and time zones add more.
    /// </summary>
    /// <remarks>
    /// This also rejects a match on numbering alone: a catalog "Mickey Mouse
    /// Clubhouse" has an S2E7 from 2008, which is not the S2E7 that aired this
    /// week.
    /// </remarks>
    private static readonly TimeSpan AddedSlack = TimeSpan.FromDays(2);

    /// <summary>
    /// Pairs catalog series with schedule shows that aired inside the window.
    /// </summary>
    /// <returns>Series id to its episodes aired inside the window, latest first.</returns>
    public static IReadOnlyDictionary<string, IReadOnlyList<AiredEpisode>> FindCandidates(
        IReadOnlyList<SeriesItem> series,
        IReadOnlyList<AiredEpisode> aired,
        DateTimeOffset now,
        TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(aired);

        var result = new Dictionary<string, IReadOnlyList<AiredEpisode>>(StringComparer.Ordinal);

        if (series.Count == 0 || aired.Count == 0)
        {
            return result;
        }

        var from = now - window;

        // Episodes per schedule show, grouped by name. Several shows can share a
        // name ("Survivor" in more than one country), so the year guard below
        // picks between them.
        var byName = new Dictionary<string, Dictionary<int, List<AiredEpisode>>>(StringComparer.Ordinal);

        foreach (var episode in aired)
        {
            if (episode.AiredAt < from || episode.AiredAt > now)
            {
                continue;
            }

            var key = NormalizeTitle(episode.ShowName).Key;
            if (key.Length == 0)
            {
                continue;
            }

            if (!byName.TryGetValue(key, out var shows))
            {
                shows = [];
                byName[key] = shows;
            }

            if (!shows.TryGetValue(episode.ShowId, out var episodes))
            {
                episodes = [];
                shows[episode.ShowId] = episodes;
            }

            episodes.Add(episode);
        }

        foreach (var item in series)
        {
            var (key, titleYear) = NormalizeTitle(item.Name);

            if (key.Length == 0 || !byName.TryGetValue(key, out var shows))
            {
                continue;
            }

            List<AiredEpisode>? matched = null;

            foreach (var episodes in shows.Values)
            {
                // Only a year in the title is trusted. The provider's release
                // date tracks the latest season as often as the first: Shark Tank
                // claims 2024 and FBI 2026, which would reject both.
                if (titleYear is { } y
                    && episodes[0].PremieredYear is { } premiered
                    && Math.Abs(y - premiered) > 1)
                {
                    continue;
                }

                (matched ??= []).AddRange(episodes);
            }

            if (matched is not null)
            {
                matched.Sort(static (a, b) => b.AiredAt.CompareTo(a.AiredAt));
                result[item.Id] = matched;
            }
        }

        return result;
    }

    /// <summary>
    /// The latest of a series' aired episodes that the provider actually has.
    /// </summary>
    /// <param name="aired">Candidate episodes, latest first.</param>
    /// <param name="provider">The provider's episodes for the series.</param>
    /// <remarks>
    /// Matched on season and episode number. A show the schedule numbers
    /// differently from the provider (by year, or with specials counted) is
    /// missed rather than guessed at: showing a series whose new episode is not
    /// there is the failure this exists to prevent.
    /// </remarks>
    public static AiredEpisode? FindAvailable(
        IReadOnlyList<AiredEpisode> aired, IReadOnlyCollection<ProviderEpisode> provider)
    {
        ArgumentNullException.ThrowIfNull(aired);
        ArgumentNullException.ThrowIfNull(provider);

        foreach (var episode in aired)
        {
            if (episode.Season is not { } season || episode.Number is not { } number)
            {
                continue;
            }

            foreach (var candidate in provider)
            {
                if (candidate.Season == season
                    && candidate.Number == number
                    && (candidate.Added is null || candidate.Added >= episode.AiredAt - AddedSlack))
                {
                    return episode;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Key for a show title, with provider decoration removed, and the year the
    /// title carried, if any.
    /// </summary>
    internal static (string Key, int? Year) NormalizeTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (string.Empty, null);
        }

        var text = value.Trim();
        int? year = null;

        var trailingYear = TrailingYear().Match(text);
        if (trailingYear.Success && trailingYear.Index > 0)
        {
            year = int.Parse(trailingYear.Groups["year"].ValueSpan, provider: null);
            text = text[..trailingYear.Index];
        }

        var tag = LeadingTag().Match(text);
        if (tag.Success && tag.Length < text.Length)
        {
            text = text[tag.Length..];
        }

        var builder = new StringBuilder(text.Length + 8);

        foreach (var c in text)
        {
            if (c == '&')
            {
                builder.Append("and");
            }
            else if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return (builder.ToString(), year);
    }

    /// <summary>"Doc (2025)", "The Final Problem - 2026".</summary>
    [GeneratedRegex(@"\s*(?:\((?<year>(?:19|20)\d{2})\)|-\s*(?<year>(?:19|20)\d{2}))\s*$")]
    private static partial Regex TrailingYear();

    /// <summary>
    /// "EN - Show", "UK | Show". A fixed list rather than any short uppercase
    /// word, because "CSI: Vegas" and "FBI: Most Wanted" are real titles.
    /// </summary>
    [GeneratedRegex(@"^(?:EN|ENG|US|USA|UK|GB|CA|AU|NZ|IE)\s*(?:-|\||:)\s+")]
    private static partial Regex LeadingTag();
}
