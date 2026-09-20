using System.Text.RegularExpressions;

namespace XtreamPlayer.Infrastructure.Security;

/// <summary>
/// Strips provider credentials out of anything that might be logged or shown.
/// </summary>
/// <remarks>
/// <para>
/// Xtream embeds credentials directly in every media URL
/// (<c>/live/{user}/{pass}/12345.ts</c>) and in every API call
/// (<c>player_api.php?username=&amp;password=</c>). Without this they appear
/// verbatim in log files, libvlc verbose output, exception messages and any
/// error text surfaced in the UI.
/// </para>
/// <para>
/// Apply this to every sink, including the libvlc log bridge. In practice it
/// protects the password better than encrypting the database does, because the
/// database is not what gets pasted into a bug report.
/// </para>
/// </remarks>
public static partial class CredentialRedactor
{
    public const string Mask = "***";

    /// <summary>
    /// <c>/live|movie|series/{user}/{pass}/</c> — the two segments after the
    /// media kind. Also matches the tokenised edge form this provider redirects
    /// to, <c>/live/play/{token}/{id}</c>.
    /// </summary>
    /// <remarks>
    /// The <c>(?!play/)</c> guard keeps this off the edge-host form
    /// <c>/live/play/{token}/{id}</c>, where "play" is a literal path segment
    /// rather than a username. That shape is handled by
    /// <see cref="EdgeTokenPattern"/>, which masks the token while leaving the
    /// route recognisable in logs.
    /// </remarks>
    [GeneratedRegex(
        @"/(?<kind>live|movie|series)/(?!play/)(?<user>[^/?#]+)/(?<pass>[^/?#]+)/",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MediaPathPattern { get; }

    [GeneratedRegex(
        @"(?<key>\b(?:password|pass|pwd|username|user|token|auth)\s*[=:]\s*)(?<value>[^&\s""'<>,;}\]]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyValuePattern { get; }

    /// <summary>
    /// The base64-ish token the provider's edge hosts use in place of
    /// credentials. Long enough not to collide with ordinary path segments.
    /// </summary>
    /// <remarks>
    /// The class deliberately excludes '/' even though standard base64 uses it:
    /// this is a single path segment, and including '/' let the match run
    /// greedily past the segment boundary and eat the stream id after it.
    /// </remarks>
    [GeneratedRegex(
        @"/play/(?<token>[A-Za-z0-9+=_-]{24,})",
        RegexOptions.CultureInvariant)]
    private static partial Regex EdgeTokenPattern { get; }

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        // Edge tokens first: they are a bearer credential for the stream, and
        // masking them before the media-path rule keeps /play/ visible so the
        // redirect hop is still identifiable in a log.
        var result = EdgeTokenPattern.Replace(text, $"/play/{Mask}");
        result = MediaPathPattern.Replace(result, m => $"/{m.Groups["kind"].Value}/{Mask}/{Mask}/");
        result = KeyValuePattern.Replace(result, m => m.Groups["key"].Value + Mask);

        return result;
    }

    /// <summary>
    /// Redacts known literal values as well as structural patterns. Use where
    /// the active profile is known, to catch credentials appearing somewhere the
    /// patterns above do not anticipate.
    /// </summary>
    public static string Redact(string? text, params string?[] literalSecrets)
    {
        var result = Redact(text);

        foreach (var secret in literalSecrets)
        {
            // Very short values would mangle unrelated text.
            if (!string.IsNullOrWhiteSpace(secret) && secret.Length >= 4)
            {
                result = result.Replace(secret, Mask, StringComparison.Ordinal);
            }
        }

        return result;
    }
}
