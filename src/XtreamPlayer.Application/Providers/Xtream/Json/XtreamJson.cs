using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XtreamPlayer.Infrastructure.Xtream.Json;

/// <summary>
/// The serializer options used for every provider response.
/// </summary>
/// <remarks>
/// Converters are applied by a factory rather than per-property attributes.
/// Annotating every property by hand is how one gets missed, and a single
/// un-annotated string field is enough to break a whole catalog against a
/// provider that returns a number there.
/// </remarks>
public static class XtreamJson
{
    public static JsonSerializerOptions Options { get; } = Build();

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            // Providers routinely include fields the model does not know about.
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };

        options.Converters.Add(new LenientStringConverter());
        options.Converters.Add(new LenientInt64Converter());
        options.Converters.Add(new LenientDoubleConverter());
        options.Converters.Add(new LenientStringListConverter());
        options.Converters.Add(new UnixSecondsConverter());

        return options;
    }
}

/// <summary>Text handling shared by the client and the EPG parser.</summary>
public static class XtreamText
{
    /// <summary>
    /// Normalizes a provider rating to a short display string.
    /// </summary>
    /// <remarks>
    /// Direct port of <c>normalize_rating</c>. Providers send "0", "", "7",
    /// "7.50" and "-1" to mean "no rating".
    /// </remarks>
    public static string? NormalizeRating(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        if (value <= 0)
        {
            return null;
        }

        return Math.Abs(value - Math.Round(value)) < 0.001
            ? value.ToString("F0", CultureInfo.InvariantCulture)
            : value.ToString("F1", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Base64-decodes an EPG title or description, falling back to the raw text.
    /// </summary>
    /// <remarks>
    /// <para>Ports <c>decode_epg_text</c> (xtream.rs:1059), with a guard it lacked.</para>
    /// <para>
    /// A short ASCII title like "News" is itself valid base64, so a naive
    /// try-decode turns real titles into mojibake. The decoded bytes must also
    /// be valid UTF-8 and free of control characters before the decode is
    /// accepted.
    /// </para>
    /// </remarks>
    public static string DecodeEpgText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "Unknown programme";
        }

        var trimmed = raw.Trim();

        // Base64 is always a multiple of 4 characters; cheap rejection first.
        if (trimmed.Length % 4 != 0 || trimmed.Length < 4)
        {
            return trimmed;
        }

        var buffer = new byte[trimmed.Length * 3 / 4];
        if (!Convert.TryFromBase64String(trimmed, buffer, out var written) || written == 0)
        {
            return trimmed;
        }

        try
        {
            var decoded = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(buffer, 0, written);

            // Control characters mean we decoded something that was never text.
            foreach (var c in decoded)
            {
                if (char.IsControl(c) && c is not ('\n' or '\r' or '\t'))
                {
                    return trimmed;
                }
            }

            return string.IsNullOrWhiteSpace(decoded) ? trimmed : decoded;
        }
        catch (DecoderFallbackException)
        {
            return trimmed;
        }
    }

    /// <summary>
    /// Parses the provider's EPG timestamps, which are local-time strings with
    /// no zone in most deployments.
    /// </summary>
    public static DateTimeOffset? ParseEpgTimestamp(string? raw, TimeSpan offset)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();

        // Unix seconds, used by some providers for start_timestamp/stop_timestamp.
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix) && unix > 0)
        {
            return DateTimeOffset.FromUnixTimeSeconds(unix);
        }

        string[] formats =
        [
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd'T'HH:mm:ss",
            "yyyy/MM/dd HH:mm:ss",
            "yyyy-MM-dd HH:mm",
        ];

        if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
        {
            return new DateTimeOffset(parsed, offset);
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var fallback)
            ? fallback
            : null;
    }
}
