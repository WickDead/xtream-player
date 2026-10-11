using System.Globalization;
using System.Text.RegularExpressions;
using XtreamPlayer.Core.Models;

namespace XtreamPlayer.Core.Library;

/// <summary>What a channel's name says is on it.</summary>
/// <param name="Title">The event or show, with the channel label and time removed.</param>
/// <param name="Start">When the event starts; null for an always-on channel.</param>
/// <param name="Stop">When the name states an end time; usually null.</param>
/// <param name="IsAlwaysOn">A 24/7 channel showing one thing all day.</param>
/// <param name="HasDate">
/// The name gave a date, not just a time, so the day is not a guess.
/// </param>
public sealed record ChannelNameListing(
    string Title, DateTimeOffset? Start, DateTimeOffset? Stop, bool IsAlwaysOn, bool HasDate = false);

/// <summary>
/// Guide listings read from a live channel's name, for channels the XMLTV feed
/// does not cover.
/// </summary>
/// <remarks>
/// <para>
/// Event channels are renamed by the provider for each event, and the name is
/// the only schedule they have: "NHL | 02 - 7pm Capitals at Hurricanes",
/// "(FLSP 294) | hockey: Tri_City Storm vs Lincoln Stars (Home) (2026-10-02 20:05:20)".
/// 24/7 channels loop one show: "24/7 Young Sheldon". Neither has an
/// epg_channel_id. In one 12,253-channel catalog, about 3,900 names carried a
/// time or a 24/7 marker.
/// </para>
/// <para>
/// Names rarely say their time zone, so <see cref="ZoneFor"/> guesses from the
/// format. The guesses were checked against fixtures with known kickoffs.
/// </para>
/// </remarks>
public static partial class ChannelNameSchedule
{
    /// <summary>How long an event is assumed to run when its name gives no end.</summary>
    public static readonly TimeSpan DefaultEventLength = TimeSpan.FromHours(4);

    /// <summary>A stated end further out than this is a typo, not a long event.</summary>
    private static readonly TimeSpan LongestStatedLength = TimeSpan.FromHours(12);

    private static readonly TimeSpan StampGranularity = TimeSpan.FromMinutes(5);

    /// <summary>Slots the provider has not filled yet are dated 2098-12-31.</summary>
    private const int PlaceholderYear = 2090;

    private static readonly TimeZoneInfo Eastern = FindZone("America/New_York", "Eastern Standard Time");
    private static readonly TimeZoneInfo Central = FindZone("America/Chicago", "Central Standard Time");
    private static readonly TimeZoneInfo Mountain = FindZone("America/Denver", "Mountain Standard Time");
    private static readonly TimeZoneInfo Pacific = FindZone("America/Los_Angeles", "Pacific Standard Time");
    private static readonly TimeZoneInfo London = FindZone("Europe/London", "GMT Standard Time");
    private static readonly TimeZoneInfo Sydney = FindZone("Australia/Sydney", "AUS Eastern Standard Time");

    /// <summary>
    /// The listings for one channel that overlap a window.
    /// </summary>
    /// <returns>Sorted and non-overlapping, as the guide grid requires; empty when the name says nothing.</returns>
    public static IReadOnlyList<EpgProgramme> Programmes(
        LiveChannel channel,
        DateTimeOffset from,
        DateTimeOffset to,
        DateTimeOffset now,
        SportsEventMatcher? sports = null) =>
        Programmes(channel, from, to, now, TimeZoneInfo.Local, sports);

    /// <param name="sports">
    /// Official schedules; an event that names a scheduled game takes its real start.
    /// </param>
    internal static IReadOnlyList<EpgProgramme> Programmes(
        LiveChannel channel,
        DateTimeOffset from,
        DateTimeOffset to,
        DateTimeOffset now,
        TimeZoneInfo viewer,
        SportsEventMatcher? sports = null)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (Parse(channel.Name, now) is not { } listing || to <= from)
        {
            return [];
        }

        if (listing.IsAlwaysOn)
        {
            return AlwaysOn(channel.Id, listing.Title, from, to, viewer);
        }

        var start = listing.Start!.Value;
        var stated = listing.Stop is { } s && s > start && s - start <= LongestStatedLength;
        var length = stated ? listing.Stop!.Value - start : DefaultEventLength;

        // The schedule knows when it actually starts; the name only thinks it does.
        // A name with a date is only wrong by a time zone; one with just a time
        // can be a day out, as "8:15pm" left over from last night is.
        var game = sports?.Find(
            listing.Title,
            start,
            listing.HasDate ? SportsEventMatcher.DatedTolerance : SportsEventMatcher.Tolerance);
        if (game is not null)
        {
            start = game.Start;

            // DAZN publishes when a broadcast ended; ESPN does not.
            if (game.Stop is { } end && end > start && end - start <= LongestStatedLength)
            {
                length = end - start;
                stated = true;
            }
        }

        var stop = start + length;

        if (stop <= from || start >= to)
        {
            return [];
        }

        var source = game is null ? "From the channel name." : $"Time from {SourceName(game.League)}.";

        return
        [
            new EpgProgramme
            {
                XmltvChannelId = channel.Id,
                Start = start,
                Stop = stop,
                Title = listing.Title,
                Description = stated ? source : source + " The end time is an estimate.",
                IsFromChannelName = true,
            },
        ];
    }

    /// <summary>Who to credit for a time, from the game's league key.</summary>
    private static string SourceName(string league) =>
        league.StartsWith("dazn", StringComparison.Ordinal) ? "DAZN"
        : league.StartsWith("thesportsdb", StringComparison.Ordinal) ? "TheSportsDB"
        : "ESPN";

    /// <summary>How long each block of an always-on channel is.</summary>
    private static readonly TimeSpan AlwaysOnSlot = TimeSpan.FromHours(1);

    /// <summary>
    /// One block per hour on the viewer's clock, so the title is always on
    /// screen. A block per day put the title at midnight, scrolled out of view.
    /// </summary>
    private static List<EpgProgramme> AlwaysOn(
        string channelId, string title, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo viewer)
    {
        var result = new List<EpgProgramme>();

        // On the hour in the viewer's zone, which matters where the offset is
        // not a whole number of hours.
        var local = TimeZoneInfo.ConvertTime(from, viewer).DateTime;
        var start = Instant(new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0), viewer);

        for (; start < to; start += AlwaysOnSlot)
        {
            result.Add(new EpgProgramme
            {
                XmltvChannelId = channelId,
                Start = start,
                Stop = start + AlwaysOnSlot,
                Title = title,
                Description = "Always on. From the channel name.",
                IsFromChannelName = true,
            });
        }

        return result;
    }

    /// <summary>
    /// Reads a listing out of a channel name.
    /// </summary>
    /// <param name="now">Resolves names that give a time but no date, or a date but no year.</param>
    /// <returns>Null when the name has no time and no 24/7 marker, or is an unfilled slot.</returns>
    public static ChannelNameListing? Parse(string? name, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        name = name.Trim();

        if (AlwaysOnToken().IsMatch(name))
        {
            var title = AlwaysOnTitle(name);
            return title.Length == 0 ? null : new ChannelNameListing(title, null, null, IsAlwaysOn: true);
        }

        if (FindTime(name, now) is not { } found)
        {
            return null;
        }

        var cleaned = CleanTitle(name);
        return cleaned.Length == 0
            ? null
            : new ChannelNameListing(cleaned, found.Start, found.Stop, IsAlwaysOn: false, found.HasDate);
    }

    // ---- times ----------------------------------------------------------

    private readonly record struct FoundTime(DateTimeOffset Start, DateTimeOffset? Stop, bool HasDate);

    private enum TimeStyle
    {
        /// <summary>A machine-written yyyy-MM-dd stamp.</summary>
        Stamp,

        /// <summary>A 12-hour clock with am/pm.</summary>
        TwelveHour,

        /// <summary>A 24-hour clock, the European convention.</summary>
        TwentyFourHour,
    }

    /// <summary>
    /// Finds the start time, preferring the most explicit form in the name.
    /// </summary>
    /// <remarks>
    /// The first time in reading order wins, so "23:00 UK / 18:00 USA" is read
    /// as 23:00 in London. Both describe the same instant.
    /// </remarks>
    private static FoundTime? FindTime(string name, DateTimeOffset now)
    {
        if (StartStop().Match(name) is { Success: true } range)
        {
            var zone = ZoneFor(name, range, TimeStyle.Stamp);
            if (ParseStamp(range.Groups["start"].Value) is not { } start
                || ParseStamp(range.Groups["stop"].Value) is not { } stop
                || start.Year >= PlaceholderYear)
            {
                return null;
            }

            return new FoundTime(Instant(start, zone), Instant(stop, zone), HasDate: true);
        }

        if (BracketStamp().Match(name) is { Success: true } stamp)
        {
            if (ParseStamp(stamp.Groups["stamp"].Value) is not { } start || start.Year >= PlaceholderYear)
            {
                return null;
            }

            // Providers stagger the stamps a few seconds per channel to keep
            // them unique, so ESPN+'s "7:00PM ET" game is stamped 19:02:50.
            var rounded = start.AddTicks(-(start.Ticks % StampGranularity.Ticks));

            return new FoundTime(Instant(rounded, ZoneFor(name, stamp, TimeStyle.Stamp)), null, HasDate: true);
        }

        if (ClockTime(name) is not { } clock)
        {
            return null;
        }

        var tz = ZoneFor(name, clock.Match, clock.Style);
        var nowLocal = TimeZoneInfo.ConvertTime(now, tz).DateTime;

        var date = FindDate(name, nowLocal, clock.Time);

        if (date.Written)
        {
            return date.Value is { } dated && dated.Year < PlaceholderYear
                ? new FoundTime(Instant(dated, tz), null, HasDate: true)
                : null;
        }

        // No date: the provider renames these channels daily, so the occurrence
        // nearest to now is the one meant. That also keeps a 10PM game that is
        // still running at 1AM on the guide.
        var nearest = Nearest(
            new[] { -1, 0, 1 }.Select(offset => nowLocal.Date.AddDays(offset) + clock.Time),
            nowLocal);

        return new FoundTime(Instant(nearest, tz), null, HasDate: false);
    }

    private readonly record struct Clock(TimeSpan Time, TimeStyle Style, Match Match);

    private static Clock? ClockTime(string name)
    {
        foreach (Match m in TwelveHourTime().Matches(name))
        {
            var (hour, minute) = m.Groups["compact"].Success
                ? (int.Parse(m.Groups["compact"].Value[..^2], CultureInfo.InvariantCulture),
                   int.Parse(m.Groups["compact"].Value[^2..], CultureInfo.InvariantCulture))
                : (Number(m.Groups["hour"]), m.Groups["minute"].Success ? Number(m.Groups["minute"]) : 0);

            if (minute > 59 || hour is 0 or > 23)
            {
                continue;
            }

            // "19:45 pm" is a 24-hour time with a stray suffix.
            if (hour > 12)
            {
                return new Clock(new TimeSpan(hour, minute, 0), TimeStyle.TwentyFourHour, m);
            }

            var pm = char.ToLowerInvariant(m.Groups["ampm"].Value[0]) == 'p';
            hour = (hour % 12) + (pm ? 12 : 0);
            return new Clock(new TimeSpan(hour, minute, 0), TimeStyle.TwelveHour, m);
        }

        if (TwentyFourHourTime().Match(name) is { Success: true } t)
        {
            return new Clock(
                new TimeSpan(Number(t.Groups["hour"]), Number(t.Groups["minute"]), 0),
                TimeStyle.TwentyFourHour,
                t);
        }

        return null;
    }

    /// <summary>A date found in a name.</summary>
    /// <param name="Written">The name has a date in it at all.</param>
    /// <param name="Value">The date and time; null when the written date is unusable.</param>
    private readonly record struct WrittenDate(bool Written, DateTime? Value)
    {
        public static WrittenDate None => default;

        public static WrittenDate Of(DateTime? value) => new(true, value);
    }

    /// <summary>
    /// The date written in the name, combined with the time.
    /// </summary>
    /// <remarks>
    /// A missing year is the one that puts the date nearest to now. "Mon 6th",
    /// with no month, is the nearest 6th, and only if that is a Monday: anything
    /// else is a stale name left over from an earlier month.
    /// </remarks>
    private static WrittenDate FindDate(string name, DateTime nowLocal, TimeSpan time)
    {
        if (DottedDate().Match(name) is { Success: true } dotted)
        {
            return WrittenDate.Of(Date(
                Number(dotted.Groups["year"]), Number(dotted.Groups["month"]), Number(dotted.Groups["day"])) + time);
        }

        var monthDay = MonthThenDay().Match(name);
        if (!monthDay.Success)
        {
            monthDay = DayThenMonth().Match(name);
        }

        if (monthDay.Success)
        {
            var month = MonthNumber(monthDay.Groups["month"].Value);
            var day = Number(monthDay.Groups["day"]);

            return WrittenDate.Of(monthDay.Groups["year"].Success
                ? Date(Number(monthDay.Groups["year"]), month, day) + time
                : InNearestYear(month, day, time, nowLocal));
        }

        if (NumericDayMonth().Match(name) is { Success: true } numeric)
        {
            return WrittenDate.Of(InNearestYear(
                Number(numeric.Groups["month"]), Number(numeric.Groups["day"]), time, nowLocal));
        }

        if (WeekdayAndDay().Match(name) is { Success: true } weekdayDay)
        {
            var day = Number(weekdayDay.Groups["day"]);
            var nearest = NearestOrNull(
                new[] { -1, 0, 1 }
                    .Select(offset => nowLocal.AddMonths(offset))
                    .Select(month => Date(month.Year, month.Month, day) + time),
                nowLocal);

            return WrittenDate.Of(
                nearest?.DayOfWeek == Weekday(weekdayDay.Groups["weekday"].Value) ? nearest : null);
        }

        if (WeekdayAlone().Match(name) is { Success: true } weekday)
        {
            var wanted = Weekday(weekday.Groups["weekday"].Value);
            return WrittenDate.Of(Nearest(
                Enumerable.Range(-3, 7)
                    .Select(offset => nowLocal.Date.AddDays(offset))
                    .Where(d => d.DayOfWeek == wanted)
                    .Select(d => d + time),
                nowLocal));
        }

        return WrittenDate.None;
    }

    private static DateTime? InNearestYear(int month, int day, TimeSpan time, DateTime nowLocal) =>
        NearestOrNull(new[] { -1, 0, 1 }.Select(offset => Date(nowLocal.Year + offset, month, day) + time), nowLocal);

    private static DateTime? Date(int year, int month, int day) =>
        year is >= 1 and <= 9998 && month is >= 1 and <= 12
        && day >= 1 && day <= DateTime.DaysInMonth(year, month)
            ? new DateTime(year, month, day)
            : null;

    private static DateTime Nearest(IEnumerable<DateTime> candidates, DateTime nowLocal) =>
        candidates.MinBy(c => Math.Abs((c - nowLocal).Ticks));

    private static DateTime? NearestOrNull(IEnumerable<DateTime?> candidates, DateTime nowLocal) =>
        candidates.OfType<DateTime>().ToList() is { Count: > 0 } valid ? Nearest(valid, nowLocal) : null;

    private static int Number(Group group) => int.Parse(group.Value, CultureInfo.InvariantCulture);

    private static DateTime? ParseStamp(string value) =>
        DateTime.TryParseExact(
            Whitespace().Replace(value.Trim(), " "),
            ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd H:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd H:mm"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed
            : null;

    private static int MonthNumber(string value) =>
        value[..3].ToLowerInvariant() switch
        {
            "jan" => 1, "feb" => 2, "mar" => 3, "apr" => 4, "may" => 5, "jun" => 6,
            "jul" => 7, "aug" => 8, "sep" => 9, "oct" => 10, "nov" => 11, "dec" => 12,
            _ => 0,
        };

    private static DayOfWeek Weekday(string value) =>
        value[..3].ToLowerInvariant() switch
        {
            "mon" => DayOfWeek.Monday,
            "tue" => DayOfWeek.Tuesday,
            "wed" => DayOfWeek.Wednesday,
            "thu" => DayOfWeek.Thursday,
            "fri" => DayOfWeek.Friday,
            "sat" => DayOfWeek.Saturday,
            _ => DayOfWeek.Sunday,
        };

    // ---- time zones -----------------------------------------------------

    /// <summary>
    /// The zone a name's time is written in.
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item>A zone written next to the time wins: "12:00 PM ET", "7:10AM UK",
    /// "// UK Fri 11 Sep 7:30pm". GMT is read as UK local time, which is how
    /// these names use it in summer too.</item>
    /// <item>An AU tag means Sydney. Kayo's "Man City v Real Madrid
    /// (2026-10-02 04:55:00)" is the 21:00 CEST kickoff.</item>
    /// <item>A machine stamp means Eastern. ESPN+ writes both forms side by
    /// side: "Oct 02 7:00PM ET (2026-10-02 19:02:50)".</item>
    /// <item>A 12-hour clock means Eastern: US leagues and broadcasters.</item>
    /// <item>A 24-hour clock means UK: "19:45", "15.00" are European kickoffs.</item>
    /// </list>
    /// </remarks>
    private static TimeZoneInfo ZoneFor(string name, Match time, TimeStyle style)
    {
        var after = ZoneAfter().Match(name, time.Index + time.Length);
        if (after.Success && ZoneFromToken(after.Groups["zone"].Value) is { } explicitZone)
        {
            return explicitZone;
        }

        var before = ZoneBefore().Match(name[..time.Index]);
        if (before.Success && ZoneFromToken(before.Groups["zone"].Value) is { } leadingZone)
        {
            return leadingZone;
        }

        if (AustralianTag().IsMatch(name))
        {
            return Sydney;
        }

        return style == TimeStyle.TwentyFourHour ? London : Eastern;
    }

    private static TimeZoneInfo? ZoneFromToken(string token) =>
        token.ToUpperInvariant() switch
        {
            "ET" or "EST" or "EDT" or "USA" => Eastern,
            "CT" or "CST" or "CDT" => Central,
            "MT" or "MST" or "MDT" => Mountain,
            "PT" or "PST" or "PDT" => Pacific,
            "UK" or "BST" or "GMT" => London,
            "AEST" or "AEDT" => Sydney,
            _ => null,
        };

    private static TimeZoneInfo FindZone(string iana, string windows)
    {
        foreach (var id in new[] { iana, windows })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // Try the other spelling.
            }
        }

        return TimeZoneInfo.Utc;
    }

    /// <summary>A wall-clock time in a zone, as an instant.</summary>
    private static DateTimeOffset Instant(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        // A time in the spring-forward gap does not exist; the hour after does.
        if (zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    // ---- titles ---------------------------------------------------------

    /// <summary>
    /// The show on a 24/7 channel: "24/7 Young Sheldon", "News USA: Dateline 24/7".
    /// </summary>
    private static string AlwaysOnTitle(string name)
    {
        // "Fight Pass 24/7 (2019-09-25 19:00:00)": a stamp means nothing here.
        name = BracketStamp().Replace(name, " ").Trim();

        // "24/7 Law & Order: SVU": a leading marker leaves only the show.
        if (AlwaysOnToken().Match(name) is { Index: 0 } leading)
        {
            return Tidy(TrailingNumberInBrackets().Replace(name[leading.Length..], " "));
        }

        var rest = AlwaysOnToken().Replace(name, " ");
        rest = TrailingNumberInBrackets().Replace(rest, " ");

        // "FloRacing 24/7 FloRacing 247".
        rest = RepeatedName().Replace(Squashed247().Replace(rest, " "), "${word}");

        var separator = rest.IndexOfAny([':', '|']);
        if (separator >= 0)
        {
            var after = Tidy(rest[(separator + 1)..]);

            // "AFL | 24/7": the label is all there is.
            rest = after.Length > 0 ? after : rest[..separator];
        }

        return Tidy(rest);
    }

    /// <summary>
    /// The event, with the channel label, dates, times and zones removed.
    /// </summary>
    private static string CleanTitle(string name)
    {
        var text = RemoveTimes(name);

        // "(FLSP 294) | hockey: ...", "NCAAF:08|San Diego State @ Toledo": the
        // first segment names the channel, not the event.
        var segments = text.Split('|')
            .Select(Tidy)
            .Where(s => s.Length > 0)
            .ToList();

        var droppedLabel = false;
        if (segments.Count > 1)
        {
            segments.RemoveAt(0);
            droppedLabel = true;
        }

        text = string.Join(" | ", segments);

        // "GaaGo 02: ...", "LIVE EVENT 03 - ...", "PSF7: ...". After a dropped
        // label, only an unspaced one: "Formula 1: Bahrain" is the event.
        var label = NumberedLabel().Match(text);
        if (label.Success && (!droppedLabel || !label.Groups["label"].Value.Trim().Contains(' ')))
        {
            text = text[label.Length..];
        }

        text = LeadingSlotNumber().Replace(text, "");
        text = LeagueAndSlot().Replace(text, "${league} ");
        text = TrailingChannelSuffix().Replace(text, "");
        text = EmptyBrackets().Replace(text, " ");

        // Flo writes hyphens as underscores: "Tri_City", "DAY 2 _ Baku".
        text = SpacedUnderscore().Replace(text, " - ").Replace('_', '-');
        text = Tidy(text);

        // "hockey: Tri-City Storm vs ...": Flo writes its sport in lower case.
        if (text.Length > 0 && char.IsLower(text[0]))
        {
            text = char.ToUpperInvariant(text[0]) + text[1..];
        }

        return text;
    }

    /// <summary>Blanks every date, time and zone token, not just the one used.</summary>
    private static string RemoveTimes(string name)
    {
        var removed = new bool[name.Length];

        void Mark(Match m)
        {
            for (var i = m.Index; i < m.Index + m.Length; i++)
            {
                removed[i] = true;
            }

            // A zone token right after the time goes with it.
            var zone = ZoneAfter().Match(name, m.Index + m.Length);
            if (zone.Success && ZoneFromToken(zone.Groups["zone"].Value) is not null)
            {
                for (var i = zone.Index; i < zone.Index + zone.Length; i++)
                {
                    removed[i] = true;
                }
            }
        }

        foreach (var regex in new[]
                 {
                     StartStop(), BracketStamp(), DottedDate(), MonthThenDay(), DayThenMonth(),
                     NumericDayMonth(), WeekdayAndDay(), TwelveHourTime(), TwentyFourHourTime(),
                 })
        {
            foreach (Match m in regex.Matches(name))
            {
                // "Day 4 Oct 02": once "Oct 02" is gone, "4 Oct" is not a date.
                if (regex == DayThenMonth() && removed[m.Groups["month"].Index])
                {
                    continue;
                }

                Mark(m);
            }
        }

        // "// UK Sun 27 Oct": the zone before a removed date.
        foreach (Match m in ZoneBeforeDate().Matches(name))
        {
            var zone = m.Groups["zone"];
            if (zone.Index + zone.Length < name.Length && removed[m.Index + m.Length]
                && ZoneFromToken(zone.Value) is not null)
            {
                for (var i = zone.Index; i < zone.Index + zone.Length; i++)
                {
                    removed[i] = true;
                }
            }
        }

        // A weekday next to a removed time is part of it: "Fri 19:00".
        foreach (Match m in WeekdayAlone().Matches(name))
        {
            var end = m.Index + m.Length;
            var next = end;
            while (next < name.Length && name[next] == ' ')
            {
                next++;
            }

            if (next < name.Length && removed[next])
            {
                for (var i = m.Index; i < end; i++)
                {
                    removed[i] = true;
                }
            }
        }

        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (removed[i])
            {
                chars[i] = ' ';
            }
        }

        return SlashSeparators().Replace(new string(chars), " ");
    }

    private static string Tidy(string text)
    {
        text = Whitespace().Replace(text, " ");
        return text.Trim(' ', ':', '-', '/', '@', ',', '|');
    }

    // ---- patterns -------------------------------------------------------

    private const string Month =
        "(?<month>jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?|sept?(?:ember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)";

    private const string WeekdayName =
        "(?:mon(?:day)?|tue(?:s(?:day)?)?|wed(?:nesday)?|thu(?:rs(?:day)?)?|fri(?:day)?|sat(?:urday)?|sun(?:day)?)";

    private const string ZoneToken = "(?<zone>ET|EST|EDT|CT|CST|CDT|MT|MST|MDT|PT|PST|PDT|UK|BST|GMT|USA|AEST|AEDT)";

    [GeneratedRegex(@"(?<!\d)24\s*/\s*7(?!\d)")]
    private static partial Regex AlwaysOnToken();

    [GeneratedRegex(@"\(\s*\d+\s*\)\s*$")]
    private static partial Regex TrailingNumberInBrackets();

    [GeneratedRegex(
        @"start:\s*(?<start>\d{4}-\d{2}-\d{2}\s+\d{1,2}:\d{2}(?::\d{2})?)\s*stop:\s*(?<stop>\d{4}-\d{2}-\d{2}\s+\d{1,2}:\d{2}(?::\d{2})?)",
        RegexOptions.IgnoreCase)]
    private static partial Regex StartStop();

    [GeneratedRegex(@"\(\s*(?<stamp>\d{4}-\d{2}-\d{2}\s+\d{1,2}:\d{2}(?::\d{2})?)\s*\)")]
    private static partial Regex BracketStamp();

    [GeneratedRegex(@"(?<![\d.])(?<year>(?:19|20)\d{2})\.(?<month>\d{1,2})\.(?<day>\d{1,2})(?![\d.])")]
    private static partial Regex DottedDate();

    /// <summary>"Oct 02", "Oct 2, 2026", "jul 17". Not "Oct 7:10AM", where the number is the hour.</summary>
    [GeneratedRegex(
        @"(?:\b" + WeekdayName + @"\.?,?\s+)?\b" + Month + @"\.?\s+(?<day>\d{1,2})(?:st|nd|rd|th)?(?![\d:.])(?:,?\s+(?<year>(?:19|20)\d{2})\b)?",
        RegexOptions.IgnoreCase)]
    private static partial Regex MonthThenDay();

    /// <summary>"Sat 3rd Oct", "27 Oct", "5th July 2026".</summary>
    [GeneratedRegex(
        @"(?:\b" + WeekdayName + @"\.?,?\s+)?(?<![\d:.])(?<day>\d{1,2})(?:st|nd|rd|th)?\s+" + Month + @"\b\.?(?:\s+(?<year>(?:19|20)\d{2})\b)?",
        RegexOptions.IgnoreCase)]
    private static partial Regex DayThenMonth();

    /// <summary>"22/10": day then month, as UK-written names do.</summary>
    [GeneratedRegex(@"(?<![\d/.:])(?<day>[0-3]?\d)/(?<month>[01]?\d)(?![\d/])")]
    private static partial Regex NumericDayMonth();

    /// <summary>"Mon 6th", with no month.</summary>
    [GeneratedRegex(@"\b(?<weekday>" + WeekdayName + @")\.?\s+(?<day>\d{1,2})(?:st|nd|rd|th)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WeekdayAndDay();

    [GeneratedRegex(@"\b(?<weekday>" + WeekdayName + @")\b\.?", RegexOptions.IgnoreCase)]
    private static partial Regex WeekdayAlone();

    /// <summary>"8:20pm", "10PM", "12:00 PM", "830PM", "19:45 pm".</summary>
    [GeneratedRegex(
        @"(?<![\d:.])(?:(?<compact>\d{3,4})|(?<hour>\d{1,2})(?:[:.](?<minute>\d{2}))?)\s*(?<ampm>[ap])\.?m\b\.?",
        RegexOptions.IgnoreCase)]
    private static partial Regex TwelveHourTime();

    /// <summary>"19:45", "15.00". Not "2026.04" or "22/10".</summary>
    [GeneratedRegex(@"(?<![\d:./])(?<hour>[01]?\d|2[0-3])[:.](?<minute>[0-5]\d)(?![\d:./])")]
    private static partial Regex TwentyFourHourTime();

    [GeneratedRegex(@"\G\s*" + ZoneToken + @"\b")]
    private static partial Regex ZoneAfter();

    /// <summary>"// UK Fri 11 Sep 7:30pm": the zone is written before the date.</summary>
    [GeneratedRegex(@"(?:^|//|\|)\s*" + ZoneToken + @"\s+(?:" + WeekdayName + @"\b[^/|]*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ZoneBefore();

    [GeneratedRegex(@"\b" + ZoneToken + @"\s+(?=\S)")]
    private static partial Regex ZoneBeforeDate();

    [GeneratedRegex(@"^\s*\(?AU\)?\s")]
    private static partial Regex AustralianTag();

    /// <summary>"GaaGo 02:", "LIVE EVENT 03 -", "RugbyPassTV 08ⓧ:", "(CA) Dazn  31:".</summary>
    [GeneratedRegex(@"^(?<label>[^:|]{0,30}?(?:\d|ⓧ))\s*(?::|\s-\s)\s*")]
    private static partial Regex NumberedLabel();

    /// <summary>"15 - SNF Lions at Panthers".</summary>
    [GeneratedRegex(@"^\d{1,3}\s*-\s+")]
    private static partial Regex LeadingSlotNumber();

    /// <summary>"NHL 4 Vancouver Canucks vs ...".</summary>
    [GeneratedRegex(@"^(?<league>[A-Z]{2,6})\s+\d{1,2}\s+(?=\S)")]
    private static partial Regex LeagueAndSlot();

    /// <summary>":Flo College  01", ":Paramount+  01" at the end.</summary>
    [GeneratedRegex(@"\s:\s*[^:|]*\d\s*$")]
    private static partial Regex TrailingChannelSuffix();

    [GeneratedRegex(@"\(\s*\)|\[\s*\]")]
    private static partial Regex EmptyBrackets();

    /// <summary>"DAY 2 _ Baku", "VLC 2026_ Stage 1": a dash, not a joined word.</summary>
    [GeneratedRegex(@"\s*_\s+")]
    private static partial Regex SpacedUnderscore();

    [GeneratedRegex(@"(?<![\d/])\b247\b(?![\d/])")]
    private static partial Regex Squashed247();

    [GeneratedRegex(@"\b(?<word>\w+)\s+\k<word>\b", RegexOptions.IgnoreCase)]
    private static partial Regex RepeatedName();

    [GeneratedRegex(@"\s//\s|\s/\s")]
    private static partial Regex SlashSeparators();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
