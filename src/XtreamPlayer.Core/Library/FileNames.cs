using System.Text;

namespace XtreamPlayer.Core.Library;

/// <summary>
/// Turns a provider title into a filename Windows will actually accept.
/// </summary>
/// <remarks>
/// Provider titles contain colons, slashes, quotes and emoji, any of which makes
/// a path invalid. Windows additionally reserves a handful of device names and
/// rejects trailing dots and spaces — the last of which is the one that catches
/// people out, because "Movie." looks perfectly reasonable.
/// </remarks>
public static class FileNames
{
    /// <summary>
    /// Device names reserved by Windows, with or without an extension, in any
    /// casing. A file called <c>CON.mkv</c> cannot be created at all.
    /// </summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Leaves room for the directory, the extension and the ".part" suffix
    /// inside the 260-character path limit.
    /// </summary>
    private const int MaxLength = 120;

    public static string Sanitize(string? title, string fallback = "download")
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return fallback;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(title.Length);
        var lastWasSpace = false;

        foreach (var c in title)
        {
            if (invalid.Contains(c) || char.IsControl(c))
            {
                // Collapse runs of replaced characters rather than leaving a
                // trail of underscores where "Title: Subtitle" used to be.
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            // Collapses a real space too, not just replaced characters: the
            // common shape is "Alien: Covenant", where the colon becomes a space
            // and the space after it would otherwise double it up.
            if (c == ' ' && lastWasSpace)
            {
                continue;
            }

            builder.Append(c);
            lastWasSpace = c == ' ';
        }

        var name = builder.ToString().Trim();

        if (name.Length > MaxLength)
        {
            name = name[..MaxLength].TrimEnd();
        }

        // Windows silently strips these, so a file named "Movie." becomes
        // "Movie" and any later lookup by the original name fails.
        name = name.TrimEnd('.', ' ');

        if (name.Length == 0)
        {
            return fallback;
        }

        // A reserved name is rejected outright, extension or not.
        var withoutExtension = Path.GetFileNameWithoutExtension(name);

        if (Reserved.Contains(withoutExtension))
        {
            name = "_" + name;
        }

        return name;
    }

    /// <summary>
    /// Builds a unique path, appending a counter rather than overwriting.
    /// </summary>
    public static string Unique(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);

        if (!File.Exists(path) && !File.Exists(path + ".part"))
        {
            return path;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({i}){extension}");

            if (!File.Exists(candidate) && !File.Exists(candidate + ".part"))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{stem} ({Guid.NewGuid():N}){extension}");
    }

    /// <summary>
    /// Where an episode belongs: <c>Series/Season 01/Series - S01E02 - Title.mkv</c>.
    /// </summary>
    public static string EpisodePath(
        string root, string seriesName, int season, int episode, string title, string extension)
    {
        var series = Sanitize(seriesName, "Series");
        var directory = Path.Combine(root, "Series", series, $"Season {season:00}");

        var fileName = Sanitize(
            $"{series} - S{season:00}E{episode:00} - {title}",
            $"S{season:00}E{episode:00}");

        return Path.Combine(directory, fileName + "." + extension.TrimStart('.'));
    }

    public static string MoviePath(string root, string title, string extension) =>
        Path.Combine(root, "Movies", Sanitize(title, "Movie") + "." + extension.TrimStart('.'));
}
