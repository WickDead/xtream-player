using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using XtreamPlayer.Core.Models;

namespace XtreamPlayer.Core.Library;

/// <summary>
/// Pairs an event read from a channel name with a game on an official schedule,
/// so the guide can use the real start time.
/// </summary>
/// <remarks>
/// <para>
/// Channel names get times wrong in ways no parser can fix: a stamp that is the
/// pre-show rather than the kickoff ("Arsenal _ Leeds United (2026-10-10 10:00:00)"
/// for an 11:30 UTC kickoff), or a time with no zone that is not Eastern after
/// all. A schedule has the real time, so a name that clearly names a scheduled
/// game takes that game's start.
/// </para>
/// <para>
/// Both sides have to match the same game, which is what keeps nicknames safe:
/// "Panthers" alone could be Florida or Carolina, but "Panthers vs Kings" on a
/// given night is one game. A name that matches nothing keeps the time it was
/// parsed with; a stale name is the provider's to fix.
/// </para>
/// </remarks>
public sealed partial class SportsEventMatcher
{
    /// <summary>
    /// How far from the parsed start a game may be when the name gave only a
    /// time: wide enough for last night's "8:15pm" read as tonight.
    /// </summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromHours(36);

    /// <summary>
    /// When the name gave a date as well, only a zone can be wrong. Wider, and
    /// "Soccer: NC State vs. Louisville" found the football game the next day.
    /// </summary>
    public static readonly TimeSpan DatedTolerance = TimeSpan.FromHours(12);

    public static readonly SportsEventMatcher Empty = new([]);

    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
    {
        "fc", "cf", "afc", "sc", "ac", "cd", "the", "and", "of", "de", "home", "away",
    };

    /// <summary>Words a side can carry that say nothing about who is playing.</summary>
    private static readonly HashSet<string> Generic = new(StringComparer.Ordinal)
    {
        "league", "premier", "premiership", "matchweek", "week", "round", "cup", "championship",
        "final", "semi", "quarter", "game", "match", "football", "soccer", "hockey", "basketball",
        "baseball", "rugby", "college", "women", "womens", "men", "mens", "live", "replay",
        "preseason", "season", "playoff", "playoffs", "regular", "spanish", "french", "english",
        "broadcast", "feed", "alt", "night", "day",
    };

    private readonly Dictionary<string, List<ScheduledGame>> _byToken = new(StringComparer.Ordinal);

    public SportsEventMatcher(IEnumerable<ScheduledGame> games)
    {
        ArgumentNullException.ThrowIfNull(games);

        foreach (var game in games)
        {
            if (game.Sides.Count != 2)
            {
                continue;
            }

            Count++;

            var tokens = game.Sides
                .SelectMany(side => side.Names)
                .SelectMany(Tokens)
                .Distinct(StringComparer.Ordinal);

            foreach (var token in tokens)
            {
                if (!_byToken.TryGetValue(token, out var list))
                {
                    _byToken[token] = list = [];
                }

                list.Add(game);
            }
        }
    }

    /// <summary>How many head-to-head games the matcher knows.</summary>
    public int Count { get; }

    /// <summary>
    /// The scheduled game an event title names, nearest to when it was thought to start.
    /// </summary>
    /// <returns>Null when the title does not name two sides of one known game.</returns>
    public ScheduledGame? Find(string title, DateTimeOffset around) => Find(title, around, Tolerance);

    /// <param name="tolerance">How far from <paramref name="around"/> the game may start.</param>
    public ScheduledGame? Find(string title, DateTimeOffset around, TimeSpan tolerance)
    {
        if (Count == 0 || SplitSides(title) is not var (first, second))
        {
            return null;
        }

        var a = Side.Of(first);
        var b = Side.Of(second);

        if (a.Tokens.Count == 0 || b.Tokens.Count == 0)
        {
            return null;
        }

        ScheduledGame? best = null;
        var bestGap = TimeSpan.MaxValue;

        foreach (var token in a.Tokens)
        {
            if (!_byToken.TryGetValue(token, out var games))
            {
                continue;
            }

            foreach (var game in games)
            {
                var gap = (game.Start - around).Duration();
                if (gap > tolerance || gap >= bestGap)
                {
                    continue;
                }

                var (x, y) = (game.Sides[0], game.Sides[1]);
                if ((Names(a, x) && Names(b, y)) || (Names(a, y) && Names(b, x)))
                {
                    best = game;
                    bestGap = gap;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Whether a side of the title names this team or fighter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every word of one of its names has to appear in the side: "NHL Florida
    /// Panthers" names the Panthers, "SNF Lions" names Detroit. A single-word
    /// name only counts against a single-word side when it is an abbreviation,
    /// so "BKN" finds Brooklyn but a stray "MIN" in a longer side does not find
    /// Minnesota.
    /// </para>
    /// <para>
    /// And the side may not say anything else of substance. College nicknames
    /// repeat everywhere, and place names nest: without this, "Cal Poly Mustangs
    /// at Eastern Washington Eagles" was SMU against Boston College, and "West
    /// Florida" was Florida. Rankings, all-caps tags (NHL, SNF, CBSSN) and
    /// generic words (league, round, women) are allowed through.
    /// </para>
    /// </remarks>
    private static bool Names(Side side, GameSide team)
    {
        var teamWords = team.Names.SelectMany(Tokens).ToHashSet(StringComparer.Ordinal);

        if (!side.Tokens.All(t => teamWords.Contains(t) || side.Filler.Contains(t)))
        {
            return false;
        }

        foreach (var name in team.Names)
        {
            var words = Tokens(name).ToList();

            if (words.Count == 0)
            {
                continue;
            }

            var isAbbreviation = words.Count == 1 && name.Length <= 4 && name.All(char.IsUpper);
            if (isAbbreviation && side.Tokens.Count != 1)
            {
                continue;
            }

            if (words.All(side.Tokens.Contains))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One side of a title: its words, and which of them may be left over.</summary>
    private sealed class Side
    {
        private Side(HashSet<string> tokens, HashSet<string> filler)
        {
            Tokens = tokens;
            Filler = filler;
        }

        public HashSet<string> Tokens { get; }

        /// <summary>Words that say nothing about who is playing.</summary>
        public HashSet<string> Filler { get; }

        public static Side Of(string text)
        {
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            var filler = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (token, original) in Words(text))
            {
                tokens.Add(token);

                var isTag = original.Length is >= 2 and <= 6 && original.All(c => !char.IsLetter(c) || char.IsUpper(c));
                if (token.All(char.IsDigit) || isTag || Generic.Contains(token))
                {
                    filler.Add(token);
                }
            }

            return new Side(tokens, filler);
        }
    }

    /// <summary>
    /// "Rangers at Red Wings" into its two sides, with labels and notes removed.
    /// </summary>
    internal static (string First, string Second)? SplitSides(string title)
    {
        // "(Home)", "[Main Broadcast]", "(Saint Francis vs CWRU)": notes, not sides.
        var text = Bracketed().Replace(title, " ");

        var separator = Separator().Match(text);
        if (!separator.Success)
        {
            // "Premier League: Arsenal - Leeds United", once underscores are dashes.
            separator = DashSeparator().Match(text);
        }

        if (!separator.Success)
        {
            return null;
        }

        var first = text[..separator.Index];
        var second = text[(separator.Index + separator.Length)..];

        // "Hockey: Tri-City Storm", "Premier League: Arsenal": the label goes.
        var colon = first.LastIndexOf(':');
        if (colon >= 0)
        {
            first = first[(colon + 1)..];
        }

        // "Lions at Panthers | Bank of America Stadium".
        var bar = second.IndexOf('|');
        if (bar >= 0)
        {
            second = second[..bar];
        }

        return string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)
            ? null
            : (first.Trim(), second.Trim());
    }

    /// <summary>Lowercase words without accents or punctuation, minus filler.</summary>
    internal static IEnumerable<string> Tokens(string text) => Words(text).Select(w => w.Token);

    /// <summary>Each word folded for matching, alongside how it was written.</summary>
    private static IEnumerable<(string Token, string Original)> Words(string text)
    {
        var word = new StringBuilder();

        foreach (var c in text.Normalize(NormalizationForm.FormD).Append(' '))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);

            // "Hawke's Bay": the apostrophe joins, it does not split.
            if (category == UnicodeCategory.NonSpacingMark || c is '\'' or '’' or '`')
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                word.Append(c);
                continue;
            }

            if (word.Length > 0)
            {
                var original = word.ToString();
                var token = original.ToLowerInvariant();
                word.Clear();

                if (!Noise.Contains(token))
                {
                    yield return (token, original);
                }
            }
        }
    }

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"\s+(?:vs\.?|v\.?|@|at|x)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex Separator();

    [GeneratedRegex(@"\s+-\s+")]
    private static partial Regex DashSeparator();
}
