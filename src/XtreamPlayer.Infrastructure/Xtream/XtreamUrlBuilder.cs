using XtreamPlayer.Core.Library;
using XtreamPlayer.Core.Models;

namespace XtreamPlayer.Infrastructure.Xtream;

/// <summary>
/// Builds every provider URL.
/// </summary>
/// <remarks>
/// Ports the URL construction from the old Rust client, with one real bug fixed:
/// the old code interpolated the username and password into the path raw, so a
/// password containing '/', '#', '?' or '@' produced a silently malformed URL
/// and every single stream failed with no useful error. Both are escaped here.
/// </remarks>
public sealed class XtreamUrlBuilder
{
    private readonly string _baseUrl;
    private readonly string _user;
    private readonly string _pass;

    public XtreamUrlBuilder(string serverUrl, string username, string password)
    {
        _baseUrl = ServerUrl.Normalize(serverUrl);
        _user = Uri.EscapeDataString(username ?? string.Empty);
        _pass = Uri.EscapeDataString(password ?? string.Empty);
    }

    /// <summary>The player_api.php endpoint with credentials and an optional action.</summary>
    public string Api(string? action = null, params (string Key, string Value)[] extra)
    {
        var query = new List<string>
        {
            "username=" + Uri.EscapeDataString(Uri.UnescapeDataString(_user)),
            "password=" + Uri.EscapeDataString(Uri.UnescapeDataString(_pass)),
        };

        if (!string.IsNullOrWhiteSpace(action))
        {
            query.Add("action=" + Uri.EscapeDataString(action));
        }

        foreach (var (key, value) in extra)
        {
            query.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
        }

        return $"{_baseUrl}/player_api.php?{string.Join("&", query)}";
    }

    public string Xmltv() =>
        $"{_baseUrl}/xmltv.php?username={_user}&password={_pass}";

    /// <summary>
    /// Chooses the live container.
    /// </summary>
    /// <remarks>
    /// Prefers <c>ts</c>, matching the old client. That preference was the cause
    /// of live playback being broken in the webview player — hls.js will not
    /// touch raw MPEG-TS and neither will a Chromium video element — and it is
    /// the correct choice now, because libvlc decodes it natively and it has
    /// lower latency than HLS.
    /// </remarks>
    public static string ChooseLiveExtension(
        IReadOnlyList<string> allowedFormats, LiveStreamFormat preference)
    {
        switch (preference)
        {
            case LiveStreamFormat.Ts:
                return "ts";
            case LiveStreamFormat.Hls:
                return "m3u8";
        }

        var normalized = allowedFormats
            .Select(f => f.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        if (normalized.Contains("ts"))
        {
            return "ts";
        }

        return normalized.Contains("m3u8") ? "m3u8" : "ts";
    }

    public string Live(string streamId, string extension) =>
        $"{_baseUrl}/live/{_user}/{_pass}/{streamId}.{Clean(extension, "ts")}";

    public string Movie(string streamId, string? containerExtension) =>
        $"{_baseUrl}/movie/{_user}/{_pass}/{streamId}.{Clean(containerExtension, "mp4")}";

    /// <summary>
    /// Episode URL, preferring the provider's own <c>direct_source</c>.
    /// </summary>
    /// <remarks>
    /// Some providers serve episodes from a different host than the catalog, and
    /// a constructed URL simply 404s for those. Ports the same preference from
    /// <c>series_detail</c> (xtream.rs:363).
    /// </remarks>
    public string Episode(string episodeId, string? containerExtension, string? directSource)
    {
        if (!string.IsNullOrWhiteSpace(directSource)
            && Uri.TryCreate(directSource.Trim(), UriKind.Absolute, out var parsed)
            && parsed.Scheme is "http" or "https")
        {
            return directSource.Trim();
        }

        return $"{_baseUrl}/series/{_user}/{_pass}/{episodeId}.{Clean(containerExtension, "mp4")}";
    }

    /// <summary>
    /// Catch-up / archive stream for a channel with <c>tv_archive</c>.
    /// </summary>
    public string Timeshift(string streamId, int durationMinutes, DateTimeOffset start) =>
        $"{_baseUrl}/timeshift/{_user}/{_pass}/{durationMinutes}/"
      + $"{start.ToString("yyyy-MM-dd:HH-mm", System.Globalization.CultureInfo.InvariantCulture)}/"
      + $"{streamId}.ts";

    private static string Clean(string? extension, string fallback)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return fallback;
        }

        var trimmed = extension.Trim().TrimStart('.').ToLowerInvariant();

        // Providers occasionally send junk here; anything that is not a plain
        // alphanumeric token would corrupt the URL.
        return trimmed.Length is > 0 and <= 8 && trimmed.All(char.IsLetterOrDigit)
            ? trimmed
            : fallback;
    }
}
