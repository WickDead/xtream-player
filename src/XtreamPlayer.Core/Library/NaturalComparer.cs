namespace XtreamPlayer.Core.Library;

/// <summary>
/// Sorts names the way people expect, treating digit runs as numbers.
/// </summary>
/// <remarks>
/// <para>
/// IPTV catalogs are full of numbered names — "TSN 1" through "TSN 5",
/// "Sportsnet 360", "Season 2" versus "Season 10". Plain alphabetical ordering
/// puts "TSN 10" between "TSN 1" and "TSN 2", which looks broken to anyone
/// scanning a channel list.
/// </para>
/// <para>
/// Comparison is ordinal and case-insensitive outside digit runs, which keeps it
/// allocation-free and stable regardless of the machine's culture. A catalog of
/// 68,595 items is sorted on every filter change, so this runs hot.
/// </para>
/// </remarks>
public sealed class NaturalComparer : IComparer<string>
{
    public static NaturalComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var left = x.AsSpan();
        var right = y.AsSpan();
        int i = 0, j = 0;

        while (i < left.Length && j < right.Length)
        {
            var leftDigit = char.IsAsciiDigit(left[i]);
            var rightDigit = char.IsAsciiDigit(right[j]);

            if (leftDigit && rightDigit)
            {
                var leftRun = DigitRun(left, i);
                var rightRun = DigitRun(right, j);

                var result = CompareNumeric(left.Slice(i, leftRun), right.Slice(j, rightRun));
                if (result != 0)
                {
                    return result;
                }

                i += leftRun;
                j += rightRun;
                continue;
            }

            var a = char.ToUpperInvariant(left[i]);
            var b = char.ToUpperInvariant(right[j]);

            if (a != b)
            {
                return a.CompareTo(b);
            }

            i++;
            j++;
        }

        return (left.Length - i).CompareTo(right.Length - j);
    }

    private static int DigitRun(ReadOnlySpan<char> text, int start)
    {
        var length = 0;
        while (start + length < text.Length && char.IsAsciiDigit(text[start + length]))
        {
            length++;
        }

        return length;
    }

    /// <summary>
    /// Compares two digit runs as numbers, without parsing.
    /// </summary>
    /// <remarks>
    /// Parsing would overflow on the absurdly long numeric runs that appear in
    /// provider titles (release ids, timestamps). Comparing by significant
    /// length and then lexically is exact for any length.
    /// </remarks>
    private static int CompareNumeric(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        var a = left.TrimStart('0');
        var b = right.TrimStart('0');

        if (a.Length != b.Length)
        {
            return a.Length.CompareTo(b.Length);
        }

        for (var k = 0; k < a.Length; k++)
        {
            if (a[k] != b[k])
            {
                return a[k].CompareTo(b[k]);
            }
        }

        // Equal in value; the one with fewer leading zeros sorts first so the
        // ordering stays total and stable.
        return left.Length.CompareTo(right.Length);
    }
}
