using System.Diagnostics;
using Microsoft.Extensions.Logging;
using XtreamPlayer.Core.Library;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Infrastructure.Xtream;

namespace XtreamPlayer.Infrastructure.Epg;

public sealed record EpgRefreshProgress(string Message, bool IsComplete = false);

public interface IEpgService
{
    event EventHandler<EpgRefreshProgress>? Progress;

    Task<EpgRefreshState> GetStateAsync(ProfileScope scope, CancellationToken ct = default);

    /// <summary>Refreshes only when the stored guide is older than the TTL.</summary>
    Task RefreshIfStaleAsync(
        Profile profile, IReadOnlyList<LiveChannel> channels, TimeSpan ttl, CancellationToken ct = default);

    Task RefreshAsync(
        Profile profile, IReadOnlyList<LiveChannel> channels, CancellationToken ct = default);

    /// <summary>Programmes for a window, keyed by live channel id rather than XMLTV id.</summary>
    Task<IReadOnlyDictionary<string, List<EpgProgramme>>> GetGuideAsync(
        ProfileScope scope, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, EpgChannelMapping>> GetMappingsAsync(
        ProfileScope scope, CancellationToken ct = default);
}

/// <summary>
/// Fetches, parses, stores and maps the XMLTV guide.
/// </summary>
/// <remarks>
/// The previous app had none of this: <c>parse_xmltv_snapshot</c> counted
/// elements and threw the content away, and every channel's "current programme"
/// was the literal placeholder string "Use short EPG sync for now/next".
/// </remarks>
public sealed class EpgService(
    IXtreamClientFactory clients,
    IHttpClientFactory httpFactory,
    IEpgRepository repository,
    ILogger<EpgService> log) : IEpgService
{
    public event EventHandler<EpgRefreshProgress>? Progress;

    public Task<EpgRefreshState> GetStateAsync(ProfileScope scope, CancellationToken ct = default) =>
        repository.GetRefreshStateAsync(scope, ct);

    public Task<IReadOnlyDictionary<string, EpgChannelMapping>> GetMappingsAsync(
        ProfileScope scope, CancellationToken ct = default) =>
        repository.GetMappingsAsync(scope, ct);

    public async Task RefreshIfStaleAsync(
        Profile profile, IReadOnlyList<LiveChannel> channels, TimeSpan ttl, CancellationToken ct = default)
    {
        var scope = new ProfileScope(profile.Id);
        var state = await repository.GetRefreshStateAsync(scope, ct).ConfigureAwait(false);

        if (state.HasData && state.Age is { } age && age < ttl)
        {
            log.LogInformation(
                "Guide is {Age:F1}h old with {Count} programmes; not refreshing.",
                age.TotalHours, state.ProgrammeCount);
            return;
        }

        await RefreshAsync(profile, channels, ct).ConfigureAwait(false);
    }

    public async Task RefreshAsync(
        Profile profile, IReadOnlyList<LiveChannel> channels, CancellationToken ct = default)
    {
        var scope = new ProfileScope(profile.Id);
        var url = clients.For(profile).Urls.Xmltv();

        Report("Downloading the guide...");

        var stopwatch = Stopwatch.StartNew();
        await using var ingest = await repository.BeginIngestAsync(scope, ct).ConfigureAwait(false);

        try
        {
            var http = httpFactory.CreateClient(XtreamClientFactory.HttpClientName);

            // Long timeout: a full guide is commonly 50-500 MB uncompressed and
            // most servers are slow with it.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));

            using var response = await http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // A provider with no guide is common and is not a failure of the
                // app; live still works from the short-EPG endpoint.
                await ingest.FailAsync(
                    $"The provider returned {(int)response.StatusCode} for xmltv.php.", ct)
                    .ConfigureAwait(false);

                Report($"No guide available ({(int)response.StatusCode}).", complete: true);
                return;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;

            if (contentType is not null
                && contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                await ingest.FailAsync(
                    "The provider returned a web page rather than an XMLTV document.", ct)
                    .ConfigureAwait(false);

                Report("No guide available.", complete: true);
                return;
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);

            var channelCount = 0;
            var programmeCount = 0;

            var parser = new XmltvParser();

            var result = await parser.ParseAsync(
                stream,
                async (channel, token) =>
                {
                    await ingest.AddChannelAsync(channel, token).ConfigureAwait(false);

                    if (++channelCount % 200 == 0)
                    {
                        Report($"Reading the guide... {channelCount} channels");
                    }
                },
                async (programme, token) =>
                {
                    await ingest.AddProgrammeAsync(programme, token).ConfigureAwait(false);

                    if (++programmeCount % 25_000 == 0)
                    {
                        Report($"Reading the guide... {programmeCount:N0} programmes");
                    }
                },
                timeout.Token).ConfigureAwait(false);

            await ingest.CommitAsync(
                result.ChannelCount, result.ProgrammeCount, url, ct).ConfigureAwait(false);

            stopwatch.Stop();

            log.LogInformation(
                "Guide refreshed: {Channels} channels, {Programmes} programmes in {Seconds:F1}s.",
                result.ChannelCount, result.ProgrammeCount, stopwatch.Elapsed.TotalSeconds);

            await MatchChannelsAsync(scope, channels, ct).ConfigureAwait(false);

            // Anything already finished half a day ago is never displayed again.
            await repository
                .PruneAsync(scope, DateTimeOffset.UtcNow.AddHours(-12), ct)
                .ConfigureAwait(false);

            Report(
                $"Guide updated: {result.ProgrammeCount:N0} programmes across "
              + $"{result.ChannelCount:N0} channels.", complete: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Guide refresh failed.");
            await ingest.FailAsync(ex.Message, CancellationToken.None).ConfigureAwait(false);
            Report($"Guide refresh failed: {ex.Message}", complete: true);
        }
    }

    /// <summary>
    /// Maps live channels onto XMLTV ids and records how each was matched.
    /// </summary>
    private async Task MatchChannelsAsync(
        ProfileScope scope, IReadOnlyList<LiveChannel> channels, CancellationToken ct)
    {
        if (channels.Count == 0)
        {
            return;
        }

        var candidates = (await repository.GetChannelsAsync(scope, ct).ConfigureAwait(false))
            .Select(c => new EpgChannelMatcher.Candidate(c.XmltvId, c.DisplayName))
            .ToList();

        var existing = await repository.GetMappingsAsync(scope, ct).ConfigureAwait(false);

        var inputs = channels
            .Select(c => new EpgChannelMatcher.Input(c.Id, c.Name, c.EpgChannelId))
            .ToList();

        var mappings = EpgChannelMatcher.Match(inputs, candidates, existing);
        await repository.SaveMappingsAsync(scope, mappings, ct).ConfigureAwait(false);

        var matched = mappings.Count(m => m.IsMatched);

        log.LogInformation(
            "Matched {Matched} of {Total} channels to the guide ({Percent:F0}%).",
            matched, mappings.Count, matched * 100.0 / Math.Max(1, mappings.Count));
    }

    public async Task<IReadOnlyDictionary<string, List<EpgProgramme>>> GetGuideAsync(
        ProfileScope scope, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var byXmltvId = await repository.GetWindowAsync(scope, from, to, ct).ConfigureAwait(false);
        var mappings = await repository.GetMappingsAsync(scope, ct).ConfigureAwait(false);

        // Re-keyed by live channel id, because that is what the UI holds. Several
        // channels can legitimately share one XMLTV id (a channel and its
        // catch-up feed, for instance), so the lists are shared by reference
        // rather than copied.
        var result = new Dictionary<string, List<EpgProgramme>>(StringComparer.Ordinal);

        foreach (var (channelId, mapping) in mappings)
        {
            if (mapping.XmltvChannelId is { } xmltvId
                && byXmltvId.TryGetValue(xmltvId, out var programmes))
            {
                result[channelId] = programmes;
            }
        }

        return result;
    }

    private void Report(string message, bool complete = false) =>
        Progress?.Invoke(this, new EpgRefreshProgress(message, complete));
}
