using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Infrastructure.Persistence;

namespace XtreamPlayer.Infrastructure.TvMaze;

public interface IAiringScheduleService
{
    /// <summary>Episodes scheduled over the last week, de-duplicated.</summary>
    IReadOnlyList<AiredEpisode> Current { get; }

    /// <summary>Raised on a background thread after <see cref="Current"/> changes.</summary>
    event EventHandler? Changed;

    /// <summary>
    /// Loads the disk cache, once. Cheap; lets a page show the category before
    /// any fetch, so a remembered selection of it can be restored.
    /// </summary>
    Task EnsureLoadedAsync(CancellationToken ct = default);

    /// <summary>
    /// Loads the disk cache on first use, then fetches any day that is missing
    /// or recent and stale. Never throws for network failures.
    /// </summary>
    Task RefreshIfStaleAsync(CancellationToken ct = default);
}

/// <summary>
/// The last week of the public TV schedule, cached on disk a day at a time.
/// </summary>
/// <remarks>
/// <para>
/// Cached per day because past days barely change: after the first run only
/// today and yesterday are refetched, which is four calls instead of sixteen.
/// </para>
/// <para>
/// The schedule is global, not per profile, so there is one cache file.
/// Failures keep the last good data; this feeds a convenience category and
/// should never surface as an error.
/// </para>
/// </remarks>
public sealed class AiringScheduleService : IAiringScheduleService
{
    /// <summary>Bumped whenever the cached shape changes.</summary>
    private const int CacheVersion = 1;

    /// <summary>Today plus the seven days before it.</summary>
    private const int DaysCovered = 8;

    /// <summary>Days this recent can still gain or move episodes.</summary>
    private const int VolatileDays = 2;

    private static readonly TimeSpan VolatileTtl = TimeSpan.FromHours(6);

    private static readonly JsonSerializerOptions CacheJson = new() { WriteIndented = false };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AiringScheduleService> _log;
    private readonly string _cachePath;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Dictionary<DateOnly, CachedDay> _days = [];
    private volatile bool _loaded;

    public AiringScheduleService(IHttpClientFactory httpFactory, ILogger<AiringScheduleService> log, string? cacheDirectory = null)
        : this(
            httpFactory,
            log,
            Path.Combine(cacheDirectory ?? DatabasePaths.CacheDirectory, $"tvmaze-schedule.v{CacheVersion}.json.gz"),
            TimeProvider.System)
    {
    }

    internal AiringScheduleService(
        IHttpClientFactory httpFactory, ILogger<AiringScheduleService> log, string cachePath, TimeProvider clock)
    {
        _httpFactory = httpFactory;
        _log = log;
        _cachePath = cachePath;
        _clock = clock;
    }

    public IReadOnlyList<AiredEpisode> Current { get; private set; } = [];

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
            // Show what the cache has straight away; the fetch below can take a
            // few seconds.
            await LoadOnceAsync(ct).ConfigureAwait(false);

            var now = _clock.GetUtcNow();
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            var oldest = today.AddDays(-(DaysCovered - 1));

            var pruned = _days.Keys.Where(d => d < oldest || d > today).ToList();
            foreach (var date in pruned)
            {
                _days.Remove(date);
            }

            var stale = Enumerable.Range(0, DaysCovered)
                .Select(i => oldest.AddDays(i))
                .Where(date => IsStale(date, today, now))
                .ToList();

            if (stale.Count == 0)
            {
                if (pruned.Count > 0)
                {
                    Publish();
                }

                return;
            }

            var fetched = 0;
            var client = new TvMazeClient(_httpFactory.CreateClient(TvMazeClient.HttpClientName));

            foreach (var date in stale)
            {
                try
                {
                    var episodes = await client.GetDayAsync(date, ct).ConfigureAwait(false);
                    _days[date] = new CachedDay(date, now, episodes);
                    fetched++;
                }
                catch (TvMazeException ex)
                {
                    // Most likely offline. Stop rather than wait out a timeout
                    // for every remaining day.
                    _log.LogWarning(ex, "Could not fetch the TV schedule for {Date}.", date);
                    break;
                }
            }

            _log.LogInformation(
                "Fetched {Fetched} of {Stale} stale TV schedule days.", fetched, stale.Count);

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

    private bool IsStale(DateOnly date, DateOnly today, DateTimeOffset now) =>
        !_days.TryGetValue(date, out var day)
        || (date >= today.AddDays(-(VolatileDays - 1)) && now - day.FetchedAt > VolatileTtl);

    private void Publish()
    {
        // The broadcast date parameter does not line up exactly with air
        // dates, so one episode can appear under two days.
        var seen = new HashSet<int>();
        var all = new List<AiredEpisode>();

        foreach (var day in _days.Values.OrderByDescending(d => d.Date))
        {
            foreach (var episode in day.Episodes)
            {
                if (seen.Add(episode.EpisodeId))
                {
                    all.Add(episode);
                }
            }
        }

        Current = all;
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

            _days = (days ?? []).ToDictionary(d => d.Date);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or InvalidDataException)
        {
            // A corrupt cache only costs one refetch.
            _log.LogWarning(ex, "Could not read the TV schedule cache.");
            _days = [];
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
            _log.LogWarning(ex, "Could not write the TV schedule cache.");
        }
    }

    internal sealed record CachedDay(DateOnly Date, DateTimeOffset FetchedAt, List<AiredEpisode> Episodes);
}
