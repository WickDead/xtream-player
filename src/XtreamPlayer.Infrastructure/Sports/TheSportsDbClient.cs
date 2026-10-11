using System.Globalization;
using System.Net;
using System.Text.Json;
using XtreamPlayer.Core.Abstractions;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Infrastructure.Security;

namespace XtreamPlayer.Infrastructure.Sports;

/// <summary>
/// TheSportsDB, with a key the user supplies. Every sport on one day, in one request.
/// </summary>
/// <remarks>
/// Optional because the free key is capped at three events per day, which is
/// useless here; a Patreon key returns the full day. It covers what ESPN and
/// DAZN do not: regional MMA and boxing, rugby league, AFL, GAA and more.
/// </remarks>
public sealed record TheSportsDbFeed(string ApiKey) : ScheduleFeed(FeedKey)
{
    public const string FeedKey = "thesportsdb";

    public override async Task<List<ScheduledGame>> GetDayAsync(
        IHttpClientFactory http, DateOnly date, CancellationToken ct = default)
    {
        try
        {
            return await new TheSportsDbClient(http.CreateClient(TheSportsDbClient.HttpClientName))
                .GetDayAsync(ApiKey, date, ct)
                .ConfigureAwait(false);
        }
        catch (SportsDbKeyRejectedException)
        {
            // The user's to fix in Settings; it must not stop ESPN and DAZN
            // from refreshing.
            return [];
        }
    }

    // The key is a secret; a record would print it.
    public override string ToString() => Key;
}

/// <summary>TheSportsDB refused the key ("Invalid Premium API key").</summary>
public sealed class SportsDbKeyRejectedException() : Exception("TheSportsDB did not accept the API key.");

public sealed class TheSportsDbClient(HttpClient http)
{
    public const string HttpClientName = "thesportsdb";

    /// <summary>The free test key, which only ever returns three events a day.</summary>
    public const string FreeKey = "123";

    /// <summary>Every event on one UTC day.</summary>
    /// <exception cref="SportsDbKeyRejectedException">The key is not a valid one.</exception>
    public async Task<List<ScheduledGame>> GetDayAsync(string apiKey, DateOnly date, CancellationToken ct = default)
    {
        var path = $"api/v1/json/{Uri.EscapeDataString(apiKey)}/eventsday.php?d="
                   + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        try
        {
            using var response = await http
                .GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            // An unknown key is a 400 with "Invalid Premium API key".
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden)
            {
                throw new SportsDbKeyRejectedException();
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return [];
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SportsFeedException($"TheSportsDB returned {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            return Parse(json.RootElement);
        }
        catch (JsonException ex)
        {
            throw new SportsFeedException("TheSportsDB returned unreadable JSON.", ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SportsFeedException("TheSportsDB did not respond in time.");
        }
        catch (HttpRequestException ex)
        {
            // The message would carry the URL, and the URL carries the key.
            throw new SportsFeedException($"Could not reach TheSportsDB ({ex.StatusCode?.ToString() ?? ex.HttpRequestError.ToString()}).");
        }
    }

    internal static List<ScheduledGame> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Parse(document.RootElement);
    }

    /// <remarks>
    /// Team events name both teams; fight cards only have the event name, such
    /// as "ONE Friday Fights 173 Tonglampoon vs Tobias", so the sides come from
    /// either side of "vs" and fighters also go by their last word.
    /// </remarks>
    private static List<ScheduledGame> Parse(JsonElement root)
    {
        var games = new List<ScheduledGame>();

        if (!root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
        {
            return games;
        }

        foreach (var ev in events.EnumerateArray())
        {
            var name = Text(ev, "strEvent");
            if (string.IsNullOrWhiteSpace(name) || Start(ev) is not { } start)
            {
                continue;
            }

            var home = Text(ev, "strHomeTeam");
            var away = Text(ev, "strAwayTeam");

            string[]? sides = !string.IsNullOrWhiteSpace(home) && !string.IsNullOrWhiteSpace(away)
                ? [home, away]
                : Versus(name);

            if (sides is null)
            {
                continue;
            }

            var league = Text(ev, "strLeague");

            games.Add(new ScheduledGame(
                league is null ? TheSportsDbFeed.FeedKey : $"{TheSportsDbFeed.FeedKey}: {league}",
                name.Trim(),
                start,
                [Side(sides[0]), Side(sides[1])]));
        }

        return games;
    }

    /// <summary>"New York Rangers" also goes by "Rangers"; "Tonglampoon" by itself.</summary>
    private static GameSide Side(string name)
    {
        name = name.Trim();
        var names = new List<string> { name };

        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1 && !names.Contains(words[^1], StringComparer.OrdinalIgnoreCase))
        {
            names.Add(words[^1]);
        }

        return new GameSide(names);
    }

    private static string[]? Versus(string name)
    {
        var at = name.IndexOf(" vs ", StringComparison.OrdinalIgnoreCase);
        if (at <= 0)
        {
            return null;
        }

        var left = name[..at].Trim();
        var right = name[(at + 4)..].Trim().TrimEnd('.');

        // "ONE Friday Fights 173 Tonglampoon": the card name is not the fighter.
        var leftWords = left.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var fighter = leftWords.Length > 2 ? string.Join(' ', leftWords[^1..]) : left;

        return right.Length == 0 ? null : [fighter, right];
    }

    /// <summary>strTimestamp is UTC without a zone; older rows only have a date and time.</summary>
    private static DateTimeOffset? Start(JsonElement ev)
    {
        var raw = Text(ev, "strTimestamp")
                  ?? (Text(ev, "dateEvent") is { } date && Text(ev, "strTime") is { } time ? $"{date}T{time}" : null);

        return raw is not null
               && DateTimeOffset.TryParse(
                   raw, CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at
            : null;
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>The user's TheSportsDB key, kept the way provider passwords are.</summary>
public interface ISportsDbKeyStore
{
    /// <summary>The key, or null when none is set or it cannot be decrypted here.</summary>
    Task<string?> GetAsync(CancellationToken ct = default);

    /// <summary>Stores the key; null or blank removes it.</summary>
    Task SetAsync(string? key, CancellationToken ct = default);

    /// <summary>How many events the key returns for today, or null when it is refused.</summary>
    /// <exception cref="SportsFeedException">TheSportsDB could not be reached.</exception>
    Task<int?> TestAsync(string key, CancellationToken ct = default);
}

public sealed class SportsDbKeyStore(
    ISettingsRepository settings,
    ICredentialProtector protector,
    IHttpClientFactory http) : ISportsDbKeyStore
{
    public async Task<string?> GetAsync(CancellationToken ct = default)
    {
        var stored = await settings.GetAsync(SettingKeys.SportsDbApiKey, ct).ConfigureAwait(false);

        // "{scheme}:{base64}", the scheme being how it was protected.
        if (stored?.Split(':', 2) is not [var scheme, var payload])
        {
            return null;
        }

        try
        {
            var key = protector.Unprotect(
                Convert.FromBase64String(payload), PasswordProtectionExtensions.ParseStorageString(scheme));

            return string.IsNullOrWhiteSpace(key) ? null : key;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public async Task SetAsync(string? key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            await settings.RemoveAsync(SettingKeys.SportsDbApiKey, ct).ConfigureAwait(false);
            return;
        }

        var blob = Convert.ToBase64String(protector.Protect(key.Trim()));
        await settings.SetAsync(
                SettingKeys.SportsDbApiKey, $"{protector.Scheme.ToStorageString()}:{blob}", ct)
            .ConfigureAwait(false);
    }

    public async Task<int?> TestAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var games = await new TheSportsDbClient(http.CreateClient(TheSportsDbClient.HttpClientName))
                .GetDayAsync(key.Trim(), DateOnly.FromDateTime(DateTime.UtcNow), ct)
                .ConfigureAwait(false);

            return games.Count;
        }
        catch (SportsDbKeyRejectedException)
        {
            return null;
        }
    }
}
