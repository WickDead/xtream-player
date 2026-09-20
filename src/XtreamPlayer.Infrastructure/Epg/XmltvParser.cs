using System.Globalization;
using System.Xml;
using XtreamPlayer.Core.Models;

namespace XtreamPlayer.Infrastructure.Epg;

public sealed record XmltvParseResult(int ChannelCount, int ProgrammeCount);

/// <summary>
/// Streams an XMLTV document, handing out channels and programmes as they are read.
/// </summary>
/// <remarks>
/// <para>
/// Never XDocument or XmlDocument. A provider's <c>xmltv.php</c> runs to
/// hundreds of megabytes uncompressed, and materializing the tree costs several
/// times that in memory before a single row is usable. This reads forward with
/// <see cref="XmlReader"/> and never holds more than one element.
/// </para>
/// <para>
/// The parser is deliberately tolerant. Real feeds contain programmes with no
/// stop time, timestamps in four different shapes, channels referenced before
/// they are declared, and occasional malformed elements. A single bad programme
/// must not lose the other 400,000.
/// </para>
/// </remarks>
public sealed class XmltvParser
{
    /// <summary>
    /// Bounds the ingest window. A feed can carry two weeks of data for a
    /// thousand channels; nothing beyond about a week is ever looked at, and
    /// storing it all makes every query slower for no benefit.
    /// </summary>
    public TimeSpan PastWindow { get; init; } = TimeSpan.FromHours(6);

    public TimeSpan FutureWindow { get; init; } = TimeSpan.FromDays(8);

    public async Task<XmltvParseResult> ParseAsync(
        Stream stream,
        Func<EpgChannel, CancellationToken, Task> onChannel,
        Func<EpgProgramme, CancellationToken, Task> onProgramme,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var settings = new XmlReaderSettings
        {
            // Ignore, not Prohibit.
            //
            // A feed is untrusted input, so entity expansion and external entity
            // resolution must stay off - that is the XXE and billion-laughs
            // vector. But the XMLTV spec puts a DOCTYPE at the top of every
            // document, and real providers emit it:
            //
            //   <!DOCTYPE tv SYSTEM "xmltv.dtd">
            //
            // Prohibit throws on that, which would have failed on essentially
            // every real feed. Ignore skips the DTD entirely: no entities are
            // defined, so a reference to one is still an error, and nothing is
            // ever fetched from disk or the network.
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreWhitespace = true,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            Async = true,
            CheckCharacters = false,
        };

        var now = DateTimeOffset.UtcNow;
        var earliest = now - PastWindow;
        var latest = now + FutureWindow;

        var channels = 0;
        var programmes = 0;

        using var reader = XmlReader.Create(stream, settings);

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.Name)
            {
                case "channel":
                {
                    var channel = await ReadChannelAsync(reader).ConfigureAwait(false);

                    if (channel is not null)
                    {
                        await onChannel(channel, ct).ConfigureAwait(false);
                        channels++;
                    }

                    break;
                }

                case "programme":
                {
                    var programme = await ReadProgrammeAsync(reader).ConfigureAwait(false);

                    if (programme is null)
                    {
                        break;
                    }

                    // Outside the window: parsed but not stored.
                    if (programme.Stop < earliest || programme.Start > latest)
                    {
                        break;
                    }

                    await onProgramme(programme, ct).ConfigureAwait(false);
                    programmes++;
                    break;
                }
            }
        }

        return new XmltvParseResult(channels, programmes);
    }

    /// <summary>
    /// Reads one &lt;channel&gt; element.
    /// </summary>
    /// <remarks>
    /// Every branch of the loop advances the reader exactly once, and the loop
    /// itself never does. ReadElementContentAsString already positions the
    /// reader on the node AFTER the closing tag, so an unconditional Read() at
    /// the top of the loop silently skips the following sibling — which is how
    /// every channel's icon, and every other child of a programme, went missing.
    /// </remarks>
    private static async Task<EpgChannel?> ReadChannelAsync(XmlReader reader)
    {
        var id = reader.GetAttribute("id");

        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        if (reader.IsEmptyElement)
        {
            return new EpgChannel { XmltvId = id.Trim() };
        }

        var depth = reader.Depth;
        string? displayName = null;
        string? icon = null;

        // Step into the element's content.
        await reader.ReadAsync().ConfigureAwait(false);

        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                await reader.ReadAsync().ConfigureAwait(false);
                continue;
            }

            switch (reader.Name)
            {
                // Feeds often carry several display-names (full, short, number);
                // the first is the one meant for humans.
                case "display-name" when displayName is null:
                    displayName = (await reader.ReadElementContentAsStringAsync()
                        .ConfigureAwait(false)).Trim();
                    break;

                case "icon" when icon is null:
                    icon = reader.GetAttribute("src");
                    await reader.SkipAsync().ConfigureAwait(false);
                    break;

                default:
                    await reader.SkipAsync().ConfigureAwait(false);
                    break;
            }
        }

        return new EpgChannel
        {
            XmltvId = id.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
            IconUrl = string.IsNullOrWhiteSpace(icon) ? null : icon,
        };
    }

    /// <inheritdoc cref="ReadChannelAsync" />
    private static async Task<EpgProgramme?> ReadProgrammeAsync(XmlReader reader)
    {
        var channelId = reader.GetAttribute("channel");
        var start = ParseTimestamp(reader.GetAttribute("start"));
        var stop = ParseTimestamp(reader.GetAttribute("stop"));
        var isEmpty = reader.IsEmptyElement;

        if (string.IsNullOrWhiteSpace(channelId) || start is null)
        {
            // Still consume the element, or the outer loop resumes inside it.
            if (!isEmpty)
            {
                await reader.SkipAsync().ConfigureAwait(false);
            }

            return null;
        }

        string? title = null;
        string? subTitle = null;
        string? description = null;
        string? category = null;
        string? episode = null;

        if (!isEmpty)
        {
            var depth = reader.Depth;
            await reader.ReadAsync().ConfigureAwait(false);

            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                {
                    break;
                }

                if (reader.NodeType != XmlNodeType.Element)
                {
                    await reader.ReadAsync().ConfigureAwait(false);
                    continue;
                }

                switch (reader.Name)
                {
                    case "title" when title is null:
                        title = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                        break;

                    case "sub-title" when subTitle is null:
                        subTitle = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                        break;

                    case "desc" when description is null:
                        description = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                        break;

                    case "category" when category is null:
                        category = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                        break;

                    case "episode-num" when episode is null:
                        episode = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                        break;

                    default:
                        await reader.SkipAsync().ConfigureAwait(false);
                        break;
                }
            }
        }

        // A missing stop is common. Half an hour is the usual slot length and is
        // far better than discarding the programme.
        var end = stop ?? start.Value.AddMinutes(30);

        if (end <= start)
        {
            end = start.Value.AddMinutes(30);
        }

        return new EpgProgramme
        {
            XmltvChannelId = channelId.Trim(),
            Start = start.Value,
            Stop = end,
            Title = string.IsNullOrWhiteSpace(title) ? "Unknown programme" : title.Trim(),
            SubTitle = Clean(subTitle),
            Description = Clean(description),
            Category = Clean(category),
            EpisodeNumber = Clean(episode),
        };
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Parses an XMLTV timestamp.
    /// </summary>
    /// <remarks>
    /// The spec says <c>yyyyMMddHHmmss zzz</c>, but real feeds also omit the
    /// offset, omit the seconds, or use a compact offset with no space. A
    /// timestamp with no zone is read as UTC, which is the least wrong
    /// assumption: guessing local time shifts an entire guide by hours.
    /// </remarks>
    internal static DateTimeOffset? ParseTimestamp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();

        string[] withOffset =
        [
            "yyyyMMddHHmmss zzz",
            "yyyyMMddHHmmsszzz",
            "yyyyMMddHHmm zzz",
            "yyyyMMddHHmmzzz",
        ];

        if (DateTimeOffset.TryParseExact(text, withOffset, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        string[] withoutOffset = ["yyyyMMddHHmmss", "yyyyMMddHHmm"];

        if (DateTime.TryParseExact(text, withoutOffset, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var naive))
        {
            return new DateTimeOffset(naive, TimeSpan.Zero);
        }

        // Last resort, for feeds using ISO-8601.
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var fallback)
            ? fallback
            : null;
    }
}
