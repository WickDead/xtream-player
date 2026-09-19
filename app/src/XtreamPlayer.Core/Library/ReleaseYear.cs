using System.Globalization;

namespace XtreamPlayer.Core.Library;

/// <summary>
/// Pulls a year out of whatever a provider calls a release date.
/// </summary>
/// <remarks>
/// The field is free text in practice. The live provider sends
/// <c>2015-01-15</c>, but the same key elsewhere carries <c>2015</c>,
/// <c>15/01/2015</c>, <c>January 2015</c>, an empty string, or <c>0000-00-00</c>
/// from a MySQL column that was never filled in. Parsing it as a date and
/// showing nothing when that fails loses the year in every case but the first.
/// </remarks>
public static class ReleaseYear
{
    /// <summary>
    /// Nothing before this is a plausible release year for video, and nothing
    /// too far ahead is either — a bad parse otherwise shows "Mad Dogs (0000)".
    /// </summary>
    private const int Earliest = 1888;

    public static int? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var latest = DateTime.UtcNow.Year + 5;

        // Scan for the first four-digit run that reads as a year, which handles
        // every ordering without needing to know the format.
        for (var i = 0; i + 4 <= value.Length; i++)
        {
            if (!char.IsAsciiDigit(value[i]))
            {
                continue;
            }

            // A longer run of digits is an id or a timestamp, not a year.
            if (i > 0 && char.IsAsciiDigit(value[i - 1]))
            {
                continue;
            }

            if (i + 4 < value.Length && char.IsAsciiDigit(value[i + 4]))
            {
                continue;
            }

            var span = value.AsSpan(i, 4);

            if (!int.TryParse(span, NumberStyles.None, CultureInfo.InvariantCulture, out var year))
            {
                continue;
            }

            if (year >= Earliest && year <= latest)
            {
                return year;
            }
        }

        return null;
    }

    /// <summary>The year as text, or null when there is nothing worth showing.</summary>
    public static string? Format(string? value) =>
        Parse(value)?.ToString(CultureInfo.InvariantCulture);
}
