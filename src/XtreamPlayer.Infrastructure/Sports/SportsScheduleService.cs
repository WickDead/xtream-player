using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using XtreamPlayer.Core.Library;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Infrastructure.Persistence;

namespace XtreamPlayer.Infrastructure.Sports;

public interface ISportsScheduleService
{
    /// <summary>The games from yesterday to two days out, ready to match.</summary>
    SportsEventMatcher Matcher { get; }

    /// <summary>Raised on a background thread after <see cref="Matcher"/> changes.</summary>
    event EventHandler? Changed;

    /// <summary>Loads the disk cache, once. No network.</summary>
    Task EnsureLoadedAsync(CancellationToken ct = default);

    /// <summary>
    /// Loads the disk cache on first use, then fetches any feed-day that is
    /// missing or stale. Never throws for network failures.
    /// </summary>
    Task RefreshIfStaleAsync(CancellationToken ct = default);
}

/// <summary>
/// Official start times from every <see cref="ScheduleFeed.All"/>, cached on
/// disk a feed and a day at a time.
/// </summary>
/// <remarks>
/// <para>
/// ESPN takes one date per request, so a cold start is a request per feed per
/// day: about a hundred, made one at a time in the background. After that only
/// today and later are refetched, every few hours, and past days never are.
/// </para>
/// <para>
/// Days are US Eastern days, which is how ESPN files games. Failures keep the
/// last good data; this sharpens guide times and must never surface as an error.
/// </para>
/// </remarks>
public sealed class SportsScheduleService : ISportsScheduleService
{
    /// <summary>Bumped whenever the cached shape changes.</summary>
    private const int CacheVersion = 2;

    /// <summary>Times move (flex scheduling, weather), so upcoming days are rechecked.</summary>
    private static readonly TimeSpan UpcomingTtl = TimeSpan.FromHours(3);

    private static readonly TimeZoneInfo Eastern = FindEastern();

    private static readonly JsonSerializerOptions CacheJson = new() { WriteIndented = false };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<SportsScheduleService> _log;
    private readonly string _cachePath;
    private readonly TimeProvider _clock;
    private readonly IReadOnlyList<ScheduleFeed> _feeds;
    private readonly Func<CancellationToken, Task<string?>> _sportsDbKey;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Dictionary<string, CachedDay> _days = new(StringComparer.Ordinal);
    private volatile bool _loaded;

    public SportsScheduleService(
        IHttpClientFactory httpFactory, ILogger<SportsScheduleService> log, ISportsDbKeyStore sportsDbKey, string? cacheDirectory = null)
        : this(
            httpFactory,
            log,
            Path.Combine(cacheDirectory ?? DatabasePaths.CacheDirectory, $"espn-schedule.v{CacheVersion}.json.gz"),
            TimeProvider.System,
            ScheduleFeed.All,
            sportsDbKey.GetAsync)
    {
    }

    internal SportsScheduleService(
        IHttpClientFactory httpFactory,
        ILogger<SportsScheduleService> log,
        string cachePath,
        TimeProvider clock,
        IReadOnlyList<ScheduleFeed> feeds,
        Func<CancellationToken, Task<string?>>? sportsDbKey = null)
    {
        _httpFactory = httpFactory;
        _log = log;
        _cachePath = cachePath;
        _clock = clock;
        _feeds = feeds;
        _sportsDbKey = sportsDbKey ?? (_ => Task.FromResult<string?>(null));
    }

    public SportsEventMatcher Matcher { get; private set; } = SportsEventMatcher.Empty;

    public event EventHandler? Changed;

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded)
        {
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            await LoadOnceAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RefreshIfStaleAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            await LoadOnceAsync(ct).ConfigureAwait(false);

            var now = _clock.GetUtcNow();
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Eastern).DateTime);
            var dates = Enumerable.Range(-1, 4).Select(today.AddDays).ToList();

            // TheSportsDB only with the user's own key; read each run, so adding
            // or removing it in Settings takes effect on the next refresh.
            var feeds = _feeds.ToList();
            if (await _sportsDbKey(ct).ConfigureAwait(false) is { Length: > 0 } apiKey)
            {
                feeds.Add(new TheSportsDbFeed(apiKey));
            }

            var active = feeds.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);

            var pruned = _days
                .Where(pair => pair.Value.Date < dates[0] || pair.Value.Date > dates[^1]
                               || !active.Contains(pair.Value.League))
                .Select(pair => pair.Key)
                .ToList();

            foreach (var key in pruned)
            {
                _days.Remove(key);
            }

            var stale = (
                from date in dates
                from feed in feeds
                where IsStale(feed, date, today, now)
                select (feed, date)).ToList();

            if (stale.Count == 0)
            {
                if (pruned.Count > 0)
                {
                    Publish();
                }

                return;
            }

            var fetched = 0;

            foreach (var (feed, date) in stale)
            {
                try
                {
                    var games = await feed.GetDayAsync(_httpFactory, date, ct).ConfigureAwait(false);
                    _days[Key(feed, date)] = new CachedDay(feed.Key, date, now, games);
                    fetched++;
                }
                catch (SportsFeedException ex)
                {
                    // Most likely offline. Stop rather than wait out a timeout
                    // for every remaining feed.
                    _log.LogWarning(ex, "Could not fetch the {Feed} schedule for {Date}.", feed.Key, date);
                    break;
                }
            }

            _log.LogInformation("Fetched {Fetched} of {Stale} stale sports schedule days.", fetched, stale.Count);

            if (fetched > 0 || pruned.Count > 0)
            {
                Publish();
                await WriteToDiskAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsStale(ScheduleFeed feed, DateOnly date, DateOnly today, DateTimeOffset now) =>
        !_days.TryGetValue(Key(feed, date), out var day)
        || (date >= today && now - day.FetchedAt > UpcomingTtl);

    private static string Key(ScheduleFeed feed, DateOnly date) => Key(feed.Key, date);

    private static string Key(string league, DateOnly date) => $"{league}/{date:yyyy-MM-dd}";

    private async Task LoadOnceAsync(CancellationToken ct)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        await LoadFromDiskAsync(ct).ConfigureAwait(false);

        if (_days.Count > 0)
        {
            Publish();
        }
    }

    private void Publish()
    {
        Matcher = new SportsEventMatcher(_days.Values.SelectMany(d => d.Games));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadFromDiskAsync(CancellationToken ct)
    {
        if (!File.Exists(_cachePath))
        {
            return;
        }

        try
        {
            await using var file = File.OpenRead(_cachePath);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);

            var days = await JsonSerializer
                .DeserializeAsync<List<CachedDay>>(gzip, CacheJson, ct)
                .ConfigureAwait(false);

            _days = (days ?? []).ToDictionary(d => Key(d.League, d.Date), StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or InvalidDataException)
        {
            // A corrupt cache only costs one refetch.
            _log.LogWarning(ex, "Could not read the sports schedule cache.");
            _days = new Dictionary<string, CachedDay>(StringComparer.Ordinal);
        }
    }

    private async Task WriteToDiskAsync(CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);

            // Written to a temp file and moved into place, so an interrupted
            // write cannot leave a half-parsed cache behind.
            var temp = _cachePath + ".tmp";

            await using (var file = File.Create(temp))
            await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            {
                await JsonSerializer.SerializeAsync(gzip, _days.Values.ToList(), CacheJson, ct)
                    .ConfigureAwait(false);
            }

            File.Move(temp, _cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not write the sports schedule cache.");
        }
    }

    private static TimeZoneInfo FindEastern()
    {
        foreach (var id in new[] { "America/New_York", "Eastern Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // Try the other spelling.
            }
        }

        return TimeZoneInfo.Utc;
    }

    internal sealed record CachedDay(string League, DateOnly Date, DateTimeOffset FetchedAt, List<ScheduledGame> Games);
}
