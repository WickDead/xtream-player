using XtreamPlayer.Core.Models;

namespace XtreamPlayer.Core.Library;

/// <summary>
/// Works out which XMLTV channel each live channel corresponds to.
/// </summary>
/// <remarks>
/// <para>
/// This is the part of EPG support that actually fails in practice. A provider
/// supplies <c>epg_channel_id</c> on each stream and an <c>xmltv.php</c> feed,
/// and remarkably often the two do not agree: the id is blank, or it is a name
/// rather than an id, or the feed uses a different convention entirely.
/// </para>
/// <para>
/// So matching falls through tiers and records which one succeeded, so the
/// Settings screen can show what was guessed and let the user correct it.
/// </para>
/// </remarks>
public static class EpgChannelMatcher
{
    /// <summary>
    /// Trailing tokens that describe the feed rather than the channel. Stripping
    /// them is what lets "TSN 1 HD" match "TSN 1".
    /// </summary>
    private static readonly HashSet<string> NoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "hd", "fhd", "uhd", "sd", "4k", "8k", "hevc", "h265", "raw", "backup",
        "us", "usa", "uk", "ca", "can", "au", "nz", "ie", "eu",
        "east", "west", "hdtv", "tv", "channel", "ch", "plus", "alt",
    };

    public sealed record Candidate(string XmltvId, string? DisplayName);

    public sealed record Input(string ChannelId, string Name, string? EpgChannelId);

    /// <summary>
    /// Matches every channel, preserving user overrides.
    /// </summary>
    /// <param name="existing">
    /// Current mappings. Rows flagged as user overrides are never re-guessed.
    /// </param>
    public static List<EpgChannelMapping> Match(
        IReadOnlyList<Input> channels,
        IReadOnlyList<Candidate> candidates,
        IReadOnlyDictionary<string, EpgChannelMapping>? existing = null)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(candidates);

        var byExactId = new Dictionary<string, string>(StringComparer.Ordinal);
        var byNormalizedId = new Dictionary<string, string>(StringComparer.Ordinal);
        var byName = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            byExactId.TryAdd(candidate.XmltvId, candidate.XmltvId);
            byNormalizedId.TryAdd(Normalize(candidate.XmltvId), candidate.XmltvId);

            // The feed's own id is often a name, so it is worth indexing both.
            var idAsName = NormalizeName(candidate.XmltvId);
            if (idAsName.Length > 0)
            {
                byName.TryAdd(idAsName, candidate.XmltvId);
            }

            var displayName = NormalizeName(candidate.DisplayName);
            if (displayName.Length > 0)
            {
                byName.TryAdd(displayName, candidate.XmltvId);
            }
        }

        var results = new List<EpgChannelMapping>(channels.Count);

        foreach (var channel in channels)
        {
            // A choice the user made outranks anything inferred.
            if (existing is not null
                && existing.TryGetValue(channel.ChannelId, out var previous)
                && previous.IsUserOverride)
            {
                results.Add(previous);
                continue;
            }

            results.Add(MatchOne(channel, byExactId, byNormalizedId, byName));
        }

        return results;
    }

    private static EpgChannelMapping MatchOne(
        Input channel,
        Dictionary<string, string> byExactId,
        Dictionary<string, string> byNormalizedId,
        Dictionary<string, string> byName)
    {
        var epgId = channel.EpgChannelId?.Trim();

        if (!string.IsNullOrEmpty(epgId))
        {
            if (byExactId.TryGetValue(epgId, out var exact))
            {
                return Mapped(channel, exact, EpgMatchMethod.ExactId);
            }

            if (byNormalizedId.TryGetValue(Normalize(epgId), out var normalized))
            {
                return Mapped(channel, normalized, EpgMatchMethod.NormalizedId);
            }

            // The provider's "id" is frequently the channel's name.
            var asName = NormalizeName(epgId);
            if (asName.Length > 0 && byName.TryGetValue(asName, out var viaId))
            {
                return Mapped(channel, viaId, EpgMatchMethod.DisplayName);
            }
        }

        var name = NormalizeName(channel.Name);
        if (name.Length > 0 && byName.TryGetValue(name, out var byDisplay))
        {
            return Mapped(channel, byDisplay, EpgMatchMethod.DisplayName);
        }

        return new EpgChannelMapping
        {
            ChannelId = channel.ChannelId,
            XmltvChannelId = null,
            Method = EpgMatchMethod.None,
        };
    }

    private static EpgChannelMapping Mapped(Input channel, string xmltvId, EpgMatchMethod method) =>
        new()
        {
            ChannelId = channel.ChannelId,
            XmltvChannelId = xmltvId,
            Method = method,
        };

    /// <summary>Case- and punctuation-insensitive key for an id.</summary>
    internal static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        Span<char> buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        var length = 0;

        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                buffer[length++] = char.ToLowerInvariant(c);
            }
        }

        return new string(buffer[..length]);
    }

    /// <summary>
    /// Key for a channel name, with provider decoration removed.
    /// </summary>
    /// <remarks>
    /// Real names look like "Sports CA: TSN 1 HD" or "UK | BBC One FHD". The
    /// prefix before a colon or pipe is a category the provider bolted on, and
    /// the trailing tokens describe the feed quality. Neither is part of the
    /// channel's identity, and leaving them in means almost nothing matches.
    /// </remarks>
    internal static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value.Trim();

        // Drop a leading "Category: " or "REGION | " prefix.
        var separator = text.LastIndexOfAny([':', '|']);
        if (separator >= 0 && separator < text.Length - 1)
        {
            text = text[(separator + 1)..];
        }

        var tokens = text.Split(
            [' ', '\t', '-', '_', '(', ')', '[', ']', '.', '+'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Strip decoration from the end only. "HD Movies" is a real channel
        // name; "BBC One HD" is BBC One.
        var end = tokens.Length;
        while (end > 1 && NoiseTokens.Contains(tokens[end - 1]))
        {
            end--;
        }

        var builder = new System.Text.StringBuilder(text.Length);

        for (var i = 0; i < end; i++)
        {
            foreach (var c in tokens[i])
            {
                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(char.ToLowerInvariant(c));
                }
            }
        }

        return builder.ToString();
    }
}
