using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using XtreamPlayer.Core.Library;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Infrastructure.Persistence;
using XtreamPlayer.Infrastructure.Xtream;

namespace XtreamPlayer.Infrastructure.TvMaze;

public interface INewThisWeekService
{
    /// <summary>Series whose episode from the last week the provider has.</summary>
    IReadOnlySet<string> SeriesIds { get; }

    /// <summary>Raised on a background thread after <see cref="SeriesIds"/> changes.</summary>
    event EventHandler? Changed;

    /// <summary>
    /// Loads the cached schedule and the cached episode checks, without touching
    /// the network, so a page can show the row before any fetch.
    /// </summary>
    Task EnsureLoadedAsync(CancellationToken ct = default);

    /// <summary>
    /// Refreshes the schedule if stale, then checks any series not yet known
    /// against the provider. Never throws for network failures.
    /// </summary>
    Task RefreshAsync(CancellationToken ct = default);
}

/// <summary>
/// Decides which series belong in "New This Week".
/// </summary>
/// <remarks>
/// <para>
/// A title match against the schedule says an episode aired; only the
/// provider's episode list says it can be watched. So each candidate's list is
/// fetched and checked for the exact season and episode, one series at a time:
/// providers cap concurrent connections, and a burst of requests can break
/// playback that is already running.
/// </para>
/// <para>
/// Results are cached on disk per profile. An episode found stays found, so a
/// confirmed series is never fetched again while its episode is in the window.
/// A miss is rechecked once the series changes in the catalog or after
/// <see cref="MissRecheck"/>, whichever comes first.
/// </para>
/// </remarks>
public sealed class NewThisWeekService : INewThisWeekService
{
    /// <summary>Bumped whenever the cached shape changes.</summary>
    private const int CacheVersion = 1;

    private static readonly TimeSpan Window = TimeSpan.FromDays(7);

    private static readonly TimeSpan MissRecheck = TimeSpan.FromHours(6);

    /// <summary>
    /// Provider episodes older than this cannot satisfy any candidate, so they
    /// are not kept. Window plus the matcher's added-slack, with margin.
    /// </summary>
    private static readonly TimeSpan KeepEpisodesFor = TimeSpan.FromDays(14);

    /// <summary>Consecutive failures that mean the provider is down, not one series.</summary>
    private const int MaxConsecutiveFailures = 3;

    /// <summary>Republish while checking, so the row fills in as it goes.</summary>
    private const int PublishEvery = 10;

    private static readonly JsonSerializerOptions CacheJson = new() { WriteIndented = false };

    private readonly ICatalogStore _catalog;
    private readonly IAiringScheduleService _schedule;
    private readonly ILogger<NewThisWeekService> _log;
    private readonly string _cacheRoot;
    private readonly TimeProvider _clock;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _subscribeLock = new();

    private Dictionary<string, CheckedSeries> _checked = new(StringComparer.Ordinal);
    private string? _cacheProfileId;
    private bool _subscribed;
    private int _dirty;

    public NewThisWeekService(
        ICatalogStore catalog, IAiringScheduleService schedule, ILogger<NewThisWeekService> log)
        : this(catalog, schedule, log, DatabasePaths.CacheDirectory, TimeProvider.System)
    {
    }

    internal NewThisWeekService(
        ICatalogStore catalog,
        IAiringScheduleService schedule,
        ILogger<NewThisWeekService> log,
        string cacheRoot,
        TimeProvider clock)
    {
        _catalog = catalog;
        _schedule = schedule;
        _log = log;
        _cacheRoot = cacheRoot;
        _clock = clock;
    }

    public IReadOnlySet<string> SeriesIds { get; private set; } = new HashSet<string>(StringComparer.Ordinal);

    public event EventHandler? Changed;

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        await _schedule.EnsureLoadedAsync(ct).ConfigureAwait(false);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await LoadCacheForAsync(_catalog.Current.ProfileId, ct).ConfigureAwait(false);
            Publish(Candidates());
        }
        finally
        {
            _gate.Release();
        }

        lock (_subscribeLock)
        {
            if (!_subscribed)
            {
                _subscribed = true;

                // Either input changing can add or remove series. Off the
                // raising thread: the catalog raises from inside its own load.
                _catalog.Changed += (_, _) => _ = Task.Run(() => CheckAsync(CancellationToken.None));
                _schedule.Changed += (_, _) => _ = Task.Run(() => CheckAsync(CancellationToken.None));
            }
        }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _schedule.RefreshIfStaleAsync(ct).ConfigureAwait(false);
        await CheckAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a check, coalescing requests that arrive while one is running.
    /// </summary>
    private async Task CheckAsync(CancellationToken ct)
    {
        Interlocked.Exchange(ref _dirty, 1);

        // Whoever holds the gate re-runs while requests keep arriving; the outer
        // loop covers a request that lands just as the holder lets go.
        while (Volatile.Read(ref _dirty) == 1)
        {
            if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                while (Interlocked.Exchange(ref _dirty, 0) == 1)
                {
                    await CheckOnceAsync(ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // This feeds a convenience row; it must never take a page down.
                _log.LogWarning(ex, "Could not check this week's episodes.");
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private async Task CheckOnceAsync(CancellationToken ct)
    {
        var state = _catalog.Current;

        // With either input missing every series looks out of the window, and
        // pruning on that would throw away every check made so far.
        if (state.ProfileId is null || state.Series.Items.Count == 0 || _schedule.Current.Count == 0)
        {
            return;
        }

        await LoadCacheForAsync(state.ProfileId, ct).ConfigureAwait(false);

        var now = _clock.GetUtcNow();
        var candidates = Candidates();
        var series = state.Series.Items
            .Where(s => candidates.ContainsKey(s.Id))
            .ToDictionary(s => s.Id, StringComparer.Ordinal);

        var changed = false;

        // Series no longer in the window will not be asked about again.
        foreach (var id in _checked.Keys.Where(id => !candidates.ContainsKey(id)).ToList())
        {
            _checked.Remove(id);
            changed = true;
        }

        Publish(candidates);

        // Most recent airings first, so the likeliest additions show up first.
        var due = candidates
            .Where(c => series.ContainsKey(c.Key) && IsDue(c.Key, c.Value, series[c.Key], now))
            .OrderByDescending(c => c.Value[0].AiredAt)
            .Select(c => c.Key)
            .ToList();

        var checkedCount = 0;
        var failures = 0;

        foreach (var id in due)
        {
            SeriesDetail? detail;

            try
            {
                detail = await _catalog.GetSeriesDetailAsync(id, ct).ConfigureAwait(false);
                failures = 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Could not read episodes for series {SeriesId}.", id);

                if (++failures >= MaxConsecutiveFailures)
                {
                    break;
                }

                continue;
            }

            // No active profile in the catalog store; nothing can be checked.
            if (detail is null)
            {
                break;
            }

            var keepFrom = now - KeepEpisodesFor;
            var episodes = detail.EpisodesBySeason.Values
                .SelectMany(list => list)
                .Where(e => e.Added is null || e.Added >= keepFrom)
                .Select(e => new ProviderEpisode(e.SeasonNumber, e.EpisodeNumber, e.Added))
                .ToList();

            _checked[id] = new CheckedSeries(series[id].LastModified, now, episodes);
            changed = true;

            if (++checkedCount % PublishEvery == 0)
            {
                Publish(candidates);
            }
        }

        Publish(candidates);

        if (due.Count > 0)
        {
            _log.LogInformation(
                "Checked {Checked} of {Due} series for this week's episodes; {Available} available.",
                checkedCount, due.Count, SeriesIds.Count);
        }

        if (changed)
        {
            await WriteCacheAsync(state.ProfileId, ct).ConfigureAwait(false);
        }
    }

    private bool IsDue(string id, IReadOnlyList<AiredEpisode> aired, SeriesItem item, DateTimeOffset now)
    {
        if (!_checked.TryGetValue(id, out var entry))
        {
            return true;
        }

        // An episode the provider has does not go away.
        if (NewEpisodeMatcher.FindAvailable(aired, entry.Episodes) is not null)
        {
            return false;
        }

        return entry.LastModified != item.LastModified || now - entry.CheckedAt > MissRecheck;
    }

    private IReadOnlyDictionary<string, IReadOnlyList<AiredEpisode>> Candidates() =>
        NewEpisodeMatcher.FindCandidates(
            _catalog.Current.Series.Items, _schedule.Current, _clock.GetUtcNow(), Window);

    private void Publish(IReadOnlyDictionary<string, IReadOnlyList<AiredEpisode>> candidates)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (id, aired) in candidates)
        {
            if (_checked.TryGetValue(id, out var entry)
                && NewEpisodeMatcher.FindAvailable(aired, entry.Episodes) is not null)
            {
                ids.Add(id);
            }
        }

        if (ids.SetEquals(SeriesIds))
        {
            return;
        }

        SeriesIds = ids;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---- disk cache -----------------------------------------------------

    private string CacheFile(string profileId) =>
        Path.Combine(_cacheRoot, profileId, $"new-this-week.v{CacheVersion}.json.gz");

    private async Task LoadCacheForAsync(string? profileId, CancellationToken ct)
    {
        if (profileId is null || profileId == _cacheProfileId)
        {
            return;
        }

        // Series ids are per provider, so another profile's results mean nothing.
        _cacheProfileId = profileId;
        _checked = new Dictionary<string, CheckedSeries>(StringComparer.Ordinal);

        var path = CacheFile(profileId);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            await using var file = File.OpenRead(path);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);

            var entries = await JsonSerializer
                .DeserializeAsync<Dictionary<string, CheckedSeries>>(gzip, CacheJson, ct)
                .ConfigureAwait(false);

            _checked = new Dictionary<string, CheckedSeries>(entries ?? [], StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or InvalidDataException)
        {
            // A corrupt cache only costs one round of checks.
            _log.LogWarning(ex, "Could not read the New This Week cache.");
        }
    }

    private async Task WriteCacheAsync(string profileId, CancellationToken ct)
    {
        var path = CacheFile(profileId);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Written to a temp file and moved into place, so an interrupted
            // write cannot leave a half-parsed cache behind.
            var temp = path + ".tmp";

            await using (var file = File.Create(temp))
            await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            {
                await JsonSerializer.SerializeAsync(gzip, _checked, CacheJson, ct).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not write the New This Week cache.");
        }
    }

    /// <summary>
    /// What the provider had for a series when it was last checked.
    /// </summary>
    /// <param name="LastModified">The catalog's stamp at the time, to notice a change.</param>
    /// <param name="Episodes">Recently added episodes only; see <see cref="KeepEpisodesFor"/>.</param>
    internal sealed record CheckedSeries(
        DateTimeOffset? LastModified, DateTimeOffset CheckedAt, List<ProviderEpisode> Episodes);
}
