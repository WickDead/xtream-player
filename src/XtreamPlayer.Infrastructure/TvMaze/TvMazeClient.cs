using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Infrastructure.Xtream;

namespace XtreamPlayer.Infrastructure.TvMaze;

public sealed class TvMazeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Reads the public TVMaze schedule. Free and keyless.
/// </summary>
/// <remarks>
/// <para>
/// Two feeds per day: the US broadcast schedule, and the web schedule for
/// streaming releases. The web feed is fetched without a country because
/// <c>country=US</c> drops Netflix and BBC iPlayer, which have no country; it is
/// mostly Chinese platforms otherwise, so it is narrowed to English shows here.
/// </para>
/// <para>
/// TVMaze allows roughly 20 calls per 10 seconds. Callers fetch one day at a
/// time, sequentially, which stays well inside that.
/// </para>
/// </remarks>
public sealed class TvMazeClient(HttpClient http)
{
    public const string HttpClientName = "tvmaze";

    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(10);

    /// <summary>Every episode scheduled for a date, broadcast and streaming.</summary>
    public async Task<List<AiredEpisode>> GetDayAsync(DateOnly date, CancellationToken ct = default)
    {
        var day = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var broadcast = await GetScheduleAsync($"schedule?country=US&date={day}", web: false, ct)
            .ConfigureAwait(false);
        var streaming = await GetScheduleAsync($"schedule/web?date={day}", web: true, ct)
            .ConfigureAwait(false);

        broadcast.AddRange(streaming);
        return broadcast;
    }

    private async Task<List<AiredEpisode>> GetScheduleAsync(string path, bool web, CancellationToken ct)
    {
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                using var response = await http
                    .GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
                {
                    var delay = response.Headers.RetryAfter?.Delta ?? DefaultRetryDelay;
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new TvMazeException($"TVMaze returned {(int)response.StatusCode} for {path}.");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                return await ParseScheduleAsync(stream, web, ct).ConfigureAwait(false);
            }
        }
        catch (JsonException ex)
        {
            throw new TvMazeException($"TVMaze returned unreadable JSON for {path}.", ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TvMazeException($"TVMaze did not respond in time for {path}.");
        }
        catch (HttpRequestException ex)
        {
            throw new TvMazeException($"Could not reach TVMaze: {ex.Message}", ex);
        }
    }

    internal static async Task<List<AiredEpisode>> ParseScheduleAsync(
        Stream stream, bool web, CancellationToken ct = default)
    {
        var rows = await JsonSerializer
            .DeserializeAsync<List<RawScheduleEpisode>>(stream, cancellationToken: ct)
            .ConfigureAwait(false);

        return Map(rows, web);
    }

    internal static List<AiredEpisode> ParseSchedule(string json, bool web) =>
        Map(JsonSerializer.Deserialize<List<RawScheduleEpisode>>(json), web);

    private static List<AiredEpisode> Map(List<RawScheduleEpisode>? rows, bool web)
    {
        var episodes = new List<AiredEpisode>(rows?.Count ?? 0);

        foreach (var row in rows ?? [])
        {
            var show = row.Show ?? row.Embedded?.Show;

            if (show is null || string.IsNullOrWhiteSpace(show.Name))
            {
                continue;
            }

            // Without an air time there is no telling whether it is out yet.
            if (!DateTimeOffset.TryParse(
                    row.Airstamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var airedAt))
            {
                continue;
            }

            if (web && !string.Equals(show.Language, "English", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            episodes.Add(new AiredEpisode(
                row.Id,
                show.Id,
                show.Name.Trim(),
                PremieredYear(show.Premiered),
                row.Season,
                row.Number,
                row.Name,
                airedAt));
        }

        return episodes;
    }

    private static int? PremieredYear(string? premiered) =>
        premiered is { Length: >= 4 }
        && int.TryParse(premiered.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            ? year
            : null;
}

public static class TvMazeHttpRegistration
{
    public static IServiceCollection AddTvMazeHttp(this IServiceCollection services)
    {
        services.AddHttpClient(TvMazeClient.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.tvmaze.com/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd(XtreamHttpRegistration.UserAgent);

            // A schedule day is a few hundred KB; anything slower is an outage
            // and the category simply keeps its last good data.
            client.Timeout = TimeSpan.FromSeconds(20);
        });

        services.AddSingleton<IAiringScheduleService, AiringScheduleService>();

        return services;
    }
}
