using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using XtreamPlayer.Core.Models;

namespace XtreamPlayer.Infrastructure.Sports;

/// <summary>DAZN's schedule as one country sees it.</summary>
/// <param name="Key">Stable id for the cache, such as "dazn-gb".</param>
/// <param name="Country">The two-letter country DAZN filters its rights by.</param>
public sealed record DaznRegion(string Key, string Country) : ScheduleFeed(Key)
{
    /// <summary>
    /// The UK feed carries the most; the US one adds what DAZN only shows there.
    /// </summary>
    public static readonly IReadOnlyList<DaznRegion> All =
    [
        new("dazn-gb", "gb"),
        new("dazn-us", "us"),
    ];

    public override Task<List<ScheduledGame>> GetDayAsync(
        IHttpClientFactory http, DateOnly date, CancellationToken ct = default) =>
        new DaznClient(http.CreateClient(DaznClient.HttpClientName)).GetDayAsync(this, date, ct);
}

/// <summary>
/// Reads the schedule behind DAZN's public guide page. Keyless, but unofficial.
/// </summary>
/// <remarks>
/// <para>
/// This is where boxing is: Matchroom, Golden Boy, Misfits, Primetime and the
/// like, which ESPN does not list at all. It also carries a long tail of
/// football and basketball leagues, and an end time for anything already shown.
/// </para>
/// <para>
/// A "tile" is one programme. Fights and games name both sides as contestants;
/// tiles without two contestants (magazine shows, motorsport) are skipped, as
/// are weigh-ins and press conferences, which name the same two fighters as
/// the fight and would otherwise compete with it.
/// </para>
/// </remarks>
public sealed partial class DaznClient(HttpClient http)
{
    public const string HttpClientName = "dazn";

    public async Task<List<ScheduledGame>> GetDayAsync(DaznRegion region, DateOnly date, CancellationToken ct = default)
    {
        var path = "eu/v5/Epg?date=" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                   + $"&country={region.Country}&languageCode=en&openBrowse=true&timeZoneOffset=0&brand=dazn&platform=web";

        try
        {
            using var response = await http
                .GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                return [];
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SportsFeedException($"DAZN returned {(int)response.StatusCode} for {path}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            return Parse(region.Key, json.RootElement);
        }
        catch (JsonException ex)
        {
            throw new SportsFeedException($"DAZN returned unreadable JSON for {path}.", ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SportsFeedException($"DAZN did not respond in time for {path}.");
        }
        catch (HttpRequestException ex)
        {
            throw new SportsFeedException($"Could not reach DAZN: {ex.Message}", ex);
        }
    }

    internal static List<ScheduledGame> Parse(string feed, string json)
    {
        using var document = JsonDocument.Parse(json);
        return Parse(feed, document.RootElement);
    }

    private static List<ScheduledGame> Parse(string feed, JsonElement root)
    {
        var games = new List<ScheduledGame>();

        if (!root.TryGetProperty("Tiles", out var tiles) || tiles.ValueKind != JsonValueKind.Array)
        {
            return games;
        }

        foreach (var tile in tiles.EnumerateArray())
        {
            var title = Text(tile, "Title") ?? string.Empty;

            if (NotTheEvent().IsMatch(title)
                || (Date(tile, "EventStartTime") ?? Date(tile, "Start")) is not { } start
                || !tile.TryGetProperty("Contestants", out var contestants)
                || contestants.ValueKind != JsonValueKind.Array
                || contestants.GetArrayLength() != 2)
            {
                continue;
            }

            var fight = Text(tile, "Sport", "Title") is { } sport
                        && (sport.Contains("Boxing", StringComparison.OrdinalIgnoreCase)
                            || sport.Contains("MMA", StringComparison.OrdinalIgnoreCase)
                            || sport.Contains("Mixed Martial", StringComparison.OrdinalIgnoreCase));

            // "Carlisle vs. Kidderminster" names the sides more briefly than the
            // contestants ("Carlisle United"), and channels use the brief form.
            var shortNames = ShortNames(title);

            var sides = contestants.EnumerateArray()
                .Select((contestant, i) => Side(
                    Text(contestant, "Title"),
                    shortNames is { } names ? names[i] : null,
                    fight))
                .ToList();

            if (sides.Any(s => s.Names.Count == 0))
            {
                continue;
            }

            var name = title.Split(':')[0].Trim();
            var stop = Date(tile, "End");
            var competition = Text(tile, "Competition", "Title");

            games.Add(new ScheduledGame(
                competition is null ? feed : $"{feed}: {competition}",
                name,
                start,
                sides,
                stop > start ? stop : null));
        }

        return games;
    }

    private static GameSide Side(string? full, string? brief, bool fight)
    {
        var names = new List<string>();

        Add(names, full);
        Add(names, brief);

        // "Faour v Sachbazjan": fights go by surname, sometimes family name first.
        if (fight && full?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 1 } parts)
        {
            Add(names, parts[^1]);
            Add(names, parts[0]);
        }

        return new GameSide(names);
    }

    /// <summary>"Faour vs. Sachbazjan: Full Event Replay" into its two sides.</summary>
    private static string[]? ShortNames(string title)
    {
        var parts = Versus().Split(title.Split(':')[0]);
        return parts.Length == 2 ? [parts[0].Trim(), parts[1].Trim()] : null;
    }

    private static void Add(List<string> names, string? name)
    {
        if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            names.Add(name.Trim());
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Text(JsonElement element, string property, string inner) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object
            ? Text(value, inner)
            : null;

    private static DateTimeOffset? Date(JsonElement element, string property) =>
        Text(element, property) is { } raw
        && DateTimeOffset.TryParse(
            raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at
            : null;

    [GeneratedRegex(@"weigh[\s-]?in|press conference|face[\s-]?off|preview|highlights|build[\s-]?up|countdown|media day",
        RegexOptions.IgnoreCase)]
    private static partial Regex NotTheEvent();

    [GeneratedRegex(@"\s+vs?\.?\s+", RegexOptions.IgnoreCase)]
    private static partial Regex Versus();
}
