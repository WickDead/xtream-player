using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Infrastructure.Xtream;

namespace XtreamPlayer.Infrastructure.Sports;

/// <summary>A schedule source could not be read; the run stops and keeps what it had.</summary>
public sealed class SportsFeedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One schedule, fetched a day at a time and cached under <see cref="Key"/>.</summary>
public abstract record ScheduleFeed(string Key)
{
    /// <summary>Every feed the guide checks event times against.</summary>
    public static IReadOnlyList<ScheduleFeed> All { get; } = [.. EspnLeague.All, .. DaznRegion.All];

    /// <summary>The games on one day.</summary>
    /// <returns>Empty when the source has nothing for that day.</returns>
    /// <exception cref="SportsFeedException">The source could not be reached or read.</exception>
    public abstract Task<List<ScheduledGame>> GetDayAsync(
        IHttpClientFactory http, DateOnly date, CancellationToken ct = default);
}

/// <summary>A schedule ESPN publishes, one request per day.</summary>
/// <param name="Key">Stable id for the cache, such as "nhl".</param>
/// <param name="Path">The scoreboard path under <c>apis/site/v2/sports/</c>.</param>
/// <param name="Query">Extra query parameters, such as a college division.</param>
public sealed record EspnLeague(string Key, string Path, string Query = "") : ScheduleFeed(Key)
{
    public override Task<List<ScheduledGame>> GetDayAsync(
        IHttpClientFactory http, DateOnly date, CancellationToken ct = default) =>
        new EspnClient(http.CreateClient(EspnClient.HttpClientName)).GetDayAsync(this, date, ct);

    /// <summary>
    /// The leagues whose games turn up on event channels.
    /// </summary>
    /// <remarks>
    /// One request each per day, so this is a deliberate list rather than
    /// everything ESPN has. Boxing and most PPV promotions are not on ESPN at
    /// all; those keep the time from their channel name. College football is
    /// two requests because the default is the top division only.
    /// </remarks>
    public static readonly IReadOnlyList<EspnLeague> All =
    [
        new("nfl", "football/nfl"),
        new("ncaaf", "football/college-football"),
        new("ncaaf-fcs", "football/college-football", "groups=81"),
        new("cfl", "football/cfl"),
        new("nhl", "hockey/nhl"),
        new("ncaah", "hockey/mens-college-hockey"),
        new("nba", "basketball/nba"),
        new("wnba", "basketball/wnba"),
        new("ncaab", "basketball/mens-college-basketball"),
        new("ncaaw", "basketball/womens-college-basketball"),
        new("mlb", "baseball/mlb"),
        new("mls", "soccer/usa.1"),
        new("nwsl", "soccer/usa.nwsl"),
        new("epl", "soccer/eng.1"),
        new("efl", "soccer/eng.2"),
        new("spfl", "soccer/sco.1"),
        new("laliga", "soccer/esp.1"),
        new("bundesliga", "soccer/ger.1"),
        new("seriea", "soccer/ita.1"),
        new("ligue1", "soccer/fra.1"),
        new("ligamx", "soccer/mex.1"),
        new("ucl", "soccer/uefa.champions"),
        new("uel", "soccer/uefa.europa"),
        new("uecl", "soccer/uefa.europa.conf"),
        new("nations", "soccer/uefa.nations"),
        new("friendly", "soccer/fifa.friendly"),
        new("prem-rugby", "rugby/267979"),
        new("ufc", "mma/ufc"),
    ];
}

/// <summary>
/// Reads ESPN's public scoreboards. Free and keyless, but unofficial: if the
/// shape changes, guide times fall back to what the channel names say.
/// </summary>
public sealed class EspnClient(HttpClient http)
{
    public const string HttpClientName = "espn";

    /// <summary>Every game in a league on one US day.</summary>
    /// <returns>Empty when ESPN does not know the league for that day.</returns>
    public async Task<List<ScheduledGame>> GetDayAsync(EspnLeague league, DateOnly date, CancellationToken ct = default)
    {
        var path = $"{league.Path}/scoreboard?dates={date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}"
                   + (league.Query.Length > 0 ? "&" + league.Query : "");

        try
        {
            using var response = await http
                .GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            // A league ESPN no longer serves is not an outage; the rest still are useful.
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                return [];
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SportsFeedException($"ESPN returned {(int)response.StatusCode} for {path}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            return Parse(league.Key, json.RootElement);
        }
        catch (JsonException ex)
        {
            throw new SportsFeedException($"ESPN returned unreadable JSON for {path}.", ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SportsFeedException($"ESPN did not respond in time for {path}.");
        }
        catch (HttpRequestException ex)
        {
            throw new SportsFeedException($"Could not reach ESPN: {ex.Message}", ex);
        }
    }

    internal static List<ScheduledGame> Parse(string league, string json)
    {
        using var document = JsonDocument.Parse(json);
        return Parse(league, document.RootElement);
    }

    /// <summary>
    /// Team sports have one competition per event; a UFC card is one event with
    /// a competition per bout, each with its own start.
    /// </summary>
    /// <remarks>
    /// Read by hand rather than into DTOs: the scoreboard is a few hundred KB of
    /// odds, leaders and broadcasts, of which this needs a handful of fields,
    /// and an undocumented feed is safer read leniently.
    /// </remarks>
    private static List<ScheduledGame> Parse(string league, JsonElement root)
    {
        var games = new List<ScheduledGame>();

        if (!root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
        {
            return games;
        }

        foreach (var ev in events.EnumerateArray())
        {
            if (!ev.TryGetProperty("competitions", out var competitions)
                || competitions.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var eventName = Text(ev, "name") ?? string.Empty;
            var eventStart = Date(ev, "date");

            foreach (var competition in competitions.EnumerateArray())
            {
                if ((Date(competition, "date") ?? eventStart) is not { } start
                    || !competition.TryGetProperty("competitors", out var competitors)
                    || competitors.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var sides = competitors.EnumerateArray()
                    .Select(Side)
                    .OfType<GameSide>()
                    .ToList();

                if (sides.Count != 2)
                {
                    continue;
                }

                var name = competitions.GetArrayLength() == 1
                    ? eventName
                    : $"{sides[0].Names[0]} vs {sides[1].Names[0]}";

                games.Add(new ScheduledGame(league, name, start, sides));
            }
        }

        return games;
    }

    private static GameSide? Side(JsonElement competitor)
    {
        var names = new List<string>();

        if (competitor.TryGetProperty("team", out var team))
        {
            Add(names, Text(team, "displayName"));
            Add(names, Text(team, "shortDisplayName"));
            Add(names, Text(team, "name"));
            Add(names, Text(team, "abbreviation"));
        }
        else if (competitor.TryGetProperty("athlete", out var athlete))
        {
            var full = Text(athlete, "displayName");
            Add(names, full);

            // Fight names use surnames: "Jones v Sanchez". Some are written
            // family name first ("Wang Cong"), so the first word counts too; a
            // match still needs both fighters.
            if (full?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 1 } parts)
            {
                Add(names, parts[^1]);
                Add(names, parts[0]);
            }
        }

        return names.Count == 0 ? null : new GameSide(names);
    }

    private static void Add(List<string> names, string? name)
    {
        if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            names.Add(name.Trim());
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>ESPN writes "2026-10-06T23:00Z", which has no seconds.</summary>
    private static DateTimeOffset? Date(JsonElement element, string property) =>
        Text(element, property) is { } raw
        && DateTimeOffset.TryParse(
            raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at
            : null;
}

public static class EspnHttpRegistration
{
    public static IServiceCollection AddEspnHttp(this IServiceCollection services)
    {
        services.AddHttpClient(EspnClient.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://site.api.espn.com/apis/site/v2/sports/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd(XtreamHttpRegistration.UserAgent);
            client.Timeout = TimeSpan.FromSeconds(15);
        });

        services.AddHttpClient(DaznClient.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://epg.discovery.indazn.com/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd(XtreamHttpRegistration.UserAgent);
            client.Timeout = TimeSpan.FromSeconds(15);
        });

        services.AddHttpClient(TheSportsDbClient.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://www.thesportsdb.com/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd(XtreamHttpRegistration.UserAgent);

            // Every sport on a day is one response, which can be large.
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddSingleton<ISportsDbKeyStore, SportsDbKeyStore>();
        services.AddSingleton<ISportsScheduleService, SportsScheduleService>();

        return services;
    }
}
