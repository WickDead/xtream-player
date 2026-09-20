namespace XtreamPlayer.Infrastructure.Persistence;

/// <summary>
/// Where application data lives on each platform.
/// </summary>
public static class DatabasePaths
{
    public const string DatabaseFileName = "xtream-player.sqlite";

    private const string AppFolderName = "XtreamPlayer";

    /// <summary>
    /// Windows: <c>%LOCALAPPDATA%\XtreamPlayer</c>.
    /// Linux: <c>$XDG_DATA_HOME/XtreamPlayer</c> (default <c>~/.local/share</c>).
    /// macOS: <c>~/Library/Application Support/XtreamPlayer</c>.
    /// </summary>
    public static string DataDirectory
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    AppFolderName);
            }

            if (OperatingSystem.IsMacOS())
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support", AppFolderName);
            }

            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrWhiteSpace(xdg))
            {
                xdg = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "share");
            }

            return Path.Combine(xdg, AppFolderName);
        }
    }

    public static string DatabaseFile => Path.Combine(DataDirectory, DatabaseFileName);

    public static string CacheDirectory => Path.Combine(DataDirectory, "cache");

    public static string PosterCacheDirectory => Path.Combine(DataDirectory, "poster-cache");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>
    /// Databases written by the previous Tauri app, newest layout first. Offered
    /// to the user by the one-time importer, which shows each one's row counts
    /// and modified date rather than guessing.
    /// </summary>
    /// <remarks>
    /// The previous app was renamed partway through its life, so an install of
    /// any age may have written under either bundle identifier.
    /// </remarks>
    public static IReadOnlyList<string> LegacyDatabaseCandidates()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        return
        [
            Path.Combine(roaming, "io.opensourceiptv.xtreamplayer", DatabaseFileName),
            Path.Combine(roaming, "com.xtreamplayer.desktop", DatabaseFileName),
        ];
    }

    /// <summary>
    /// WebView2's LevelDB store, which holds the old app's localStorage —
    /// including the <c>xtream-hidden-categories-*</c> keys.
    /// </summary>
    /// <remarks>
    /// Reading LevelDB properly is not worth a native dependency for a handful
    /// of category names, so the importer only uses this to offer a best-effort
    /// pre-fill. The authoritative path is the user pasting the JSON exported
    /// from the old app's devtools console.
    /// </remarks>
    public static string? LegacyLocalStorageDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = Path.Combine(
            local, "io.opensourceiptv.xtreamplayer",
            "EBWebView", "Default", "Local Storage", "leveldb");

        return Directory.Exists(path) ? path : null;
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(PosterCacheDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
