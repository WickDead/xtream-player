using System.Text;

namespace XtreamPlayer.Playback;

/// <summary>
/// Turns libvlc's numeric codec identifiers into something worth showing in a
/// track menu. libvlc reports codecs as packed four-character codes.
/// </summary>
internal static class FourCc
{
    private static readonly Dictionary<string, string> FriendlyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Audio
        ["mp4a"] = "AAC",
        ["aac "] = "AAC",
        ["a52 "] = "AC-3",
        ["ac-3"] = "AC-3",
        ["eac3"] = "E-AC-3",
        ["dts "] = "DTS",
        ["mpga"] = "MP3",
        ["mp3 "] = "MP3",
        ["opus"] = "Opus",
        ["vorb"] = "Vorbis",
        ["flac"] = "FLAC",
        ["alaw"] = "A-law",
        ["ulaw"] = "u-law",
        ["araw"] = "PCM",
        ["truh"] = "TrueHD",

        // Video
        ["h264"] = "H.264",
        ["avc1"] = "H.264",
        ["hevc"] = "HEVC",
        ["hvc1"] = "HEVC",
        ["hev1"] = "HEVC",
        ["mpgv"] = "MPEG-2",
        ["mp2v"] = "MPEG-2",
        ["mp4v"] = "MPEG-4",
        ["vp08"] = "VP8",
        ["vp09"] = "VP9",
        ["av01"] = "AV1",

        // Subtitles
        ["subt"] = "Text",
        ["ssa "] = "SSA",
        ["ass "] = "ASS",
        ["srt "] = "SRT",
        ["dvbs"] = "DVB",
        ["spu "] = "VobSub",
        ["cc1 "] = "Closed captions",
        ["telx"] = "Teletext",
    };

    /// <summary>
    /// Returns a display name for a codec, or null when nothing useful is known.
    /// </summary>
    /// <param name="codec">MediaTrack.Codec — often normalized by libvlc.</param>
    /// <param name="originalFourcc">
    /// MediaTrack.OriginalFourcc — the container's own value, used as a fallback
    /// when the normalized codec is unrecognized.
    /// </param>
    public static string? Describe(uint codec, uint originalFourcc)
    {
        foreach (var candidate in new[] { codec, originalFourcc })
        {
            var text = Decode(candidate);
            if (text is null)
            {
                continue;
            }

            if (FriendlyNames.TryGetValue(text, out var friendly))
            {
                return friendly;
            }
        }

        // Unknown but printable: showing "xyz1" beats showing nothing.
        return Decode(codec)?.Trim() is { Length: > 0 } raw ? raw.ToUpperInvariant() : null;
    }

    /// <summary>Unpacks a FourCC into its four ASCII characters, or null if it isn't printable.</summary>
    private static string? Decode(uint value)
    {
        if (value == 0)
        {
            return null;
        }

        Span<char> chars = stackalloc char[4];
        for (var i = 0; i < 4; i++)
        {
            var b = (byte)(value >> (i * 8));

            // Printable ASCII only; anything else means this isn't a FourCC.
            if (b is < 0x20 or > 0x7E)
            {
                return null;
            }

            chars[i] = (char)b;
        }

        return new string(chars);
    }

    /// <summary>Kept for diagnostics logging.</summary>
    public static string ToDebugString(uint value) =>
        Decode(value) ?? "0x" + value.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
}
