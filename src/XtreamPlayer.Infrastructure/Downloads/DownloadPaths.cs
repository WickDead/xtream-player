using Microsoft.Extensions.Logging;
using XtreamPlayer.Core.Abstractions;
using XtreamPlayer.Core.Library;
using XtreamPlayer.Core.Models;

namespace XtreamPlayer.Infrastructure.Downloads;

public interface IDownloadPaths
{
    /// <summary>The configured library root, creating it if it does not exist.</summary>
    Task<string> RootAsync(CancellationToken ct = default);

    Task<string> ForMovieAsync(VodItem item, CancellationToken ct = default);

    Task<string> ForEpisodeAsync(
        string seriesName, int season, int episode, string title, string extension,
        CancellationToken ct = default);

    Task<string> ForRecordingAsync(string channelName, string? programmeTitle,
        CancellationToken ct = default);
}

/// <summary>
/// Decides where a download lands.
/// </summary>
/// <remarks>
/// Kept apart from the manager so the naming rules are testable without an
/// HTTP client, and so recordings and downloads cannot drift into two different
/// folder layouts.
/// </remarks>
public sealed class DownloadPaths(
    ISettingsRepository settings, ILogger<DownloadPaths> log) : IDownloadPaths
{
    public async Task<string> RootAsync(CancellationToken ct = default)
    {
        var configured = await settings.GetAsync(SettingKeys.DownloadsPath, ct).ConfigureAwait(false);

        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                "XtreamPlayer")
            : configured;

        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception ex)
        {
            // A removable drive that is no longer mounted, most likely. Falling
            // back beats failing the download outright, and the path is visible
            // on the job so it is obvious where it went.
            log.LogWarning(ex, "Downloads folder {Root} is unusable; falling back to Videos.", root);

            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "XtreamPlayer");

            Directory.CreateDirectory(root);
        }

        return root;
    }

    public async Task<string> ForMovieAsync(VodItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        var root = await RootAsync(ct).ConfigureAwait(false);
        var path = FileNames.MoviePath(root, item.Name, item.ContainerExtension);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        return FileNames.Unique(Path.GetDirectoryName(path)!, Path.GetFileName(path));
    }

    public async Task<string> ForEpisodeAsync(
        string seriesName, int season, int episode, string title, string extension,
        CancellationToken ct = default)
    {
        var root = await RootAsync(ct).ConfigureAwait(false);
        var path = FileNames.EpisodePath(root, seriesName, season, episode, title, extension);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        return FileNames.Unique(Path.GetDirectoryName(path)!, Path.GetFileName(path));
    }

    /// <summary>
    /// Recordings are always MPEG-TS: that is what libvlc muxes without
    /// re-encoding, and it survives a stream that stops mid-recording.
    /// </summary>
    public async Task<string> ForRecordingAsync(
        string channelName, string? programmeTitle, CancellationToken ct = default)
    {
        var root = await RootAsync(ct).ConfigureAwait(false);
        var directory = Path.Combine(root, "Recordings");

        Directory.CreateDirectory(directory);

        var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HHmm");

        var name = string.IsNullOrWhiteSpace(programmeTitle)
            ? $"{channelName} - {stamp}"
            : $"{channelName} - {programmeTitle} - {stamp}";

        return FileNames.Unique(directory, FileNames.Sanitize(name, $"Recording {stamp}") + ".ts");
    }
}
