namespace XtreamPlayer.Core.Library;

/// <summary>
/// Normalization for provider base URLs.
/// </summary>
public static class ServerUrl
{
    /// <summary>
    /// Prepends a scheme when none is given and trims trailing slashes.
    /// </summary>
    /// <remarks>
    /// Ports <c>XtreamClient::new</c> from the previous Rust client
    /// (src-tauri/src/api/xtream.rs:43). Users routinely type a bare host, and
    /// trailing slashes otherwise produce a double slash in every built URL.
    /// </remarks>
    public static string Normalize(string? serverUrl)
    {
        var trimmed = (serverUrl ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "http://" + trimmed;
        }

        return trimmed.TrimEnd('/');
    }
}
