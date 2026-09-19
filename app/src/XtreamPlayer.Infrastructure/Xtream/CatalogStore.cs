using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Infrastructure.Persistence;

namespace XtreamPlayer.Infrastructure.Xtream;

public enum CatalogKind
{
    Live,
    Vod,
    Series,
}

public sealed record CatalogState
{
    public Catalog<LiveChannel> Live { get; init; } = Catalog<LiveChannel>.Empty;
    public Catalog<VodItem> Vod { get; init; } = Catalog<VodItem>.Empty;
    public Catalog<SeriesItem> Series { get; init; } = Catalog<SeriesItem>.Empty;

    public string? ProfileId { get; init; }
    public DateTimeOffset? LoadedAt { get; init; }

    public bool IsEmpty => Live.Items.Count == 0 && Vod.Items.Count == 0 && Series.Items.Count == 0;
}

public sealed class CatalogProgress
{
    public required CatalogKind Kind { get; init; }
    public required string Message { get; init; }
    public bool IsComplete { get; init; }
}

public interface ICatalogStore
{
    CatalogState Current { get; }

    event EventHandler<CatalogState>? Changed;
    event EventHandler<CatalogProgress>? Progress;

    /// <summary>
    /// Loads a profile's catalogs, from disk when fresh enough, refreshing in
    /// the background otherwise.
    /// </summary>
    Task LoadAsync(Profile profile, bool forceRefresh = false, CancellationToken ct = default);

    Task<SeriesDetail?> GetSeriesDetailAsync(string seriesId, CancellationToken ct = default);
    Task<VodDetail?> GetVodDetailAsync(string vodId, CancellationToken ct = default);

    void Clear();
}

/// <summary>
/// Holds the active profile's catalogs in memory, backed by a compressed disk
/// cache.
/// </summary>
/// <remarks>
/// <para>
/// This is the single biggest perceived-speed change from the old app, which
/// re-fetched every catalog on every page entry. Here, pages read from memory
/// and never fetch; startup reads a gzipped snapshot from disk in well under a
/// second and only hits the network when the snapshot is stale.
/// </para>
/// <para>
/// It is also what makes a cached navigation back-stack cheap: returning to a
/// page costs nothing because the data never went away.
/// </para>
/// </remarks>
public sealed class CatalogStore(
    IXtreamClientFactory clients,
    ILogger<CatalogStore> log) : ICatalogStore
{
    private static readonly JsonSerializerOptions CacheJson = new()
    {
        // Compact: these files reach 30 MB before compression.
        WriteIndented = false,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly Dictionary<string, SeriesDetail> _seriesDetails = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VodDetail> _vodDetails = new(StringComparer.Ordinal);

    private Profile? _profile;

    public TimeSpan Ttl { get; set; } = TimeSpan.FromHours(12);

    public CatalogState Current { get; private set; } = new();

    public event EventHandler<CatalogState>? Changed;
    public event EventHandler<CatalogProgress>? Progress;

    public async Task LoadAsync(Profile profile, bool forceRefresh = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var switchingProfile = _profile?.Id != profile.Id;
            _profile = profile;

            if (switchingProfile)
            {
                // Detail caches are keyed by provider id, so they cannot carry
                // across a profile switch.
                _seriesDetails.Clear();
                _vodDetails.Clear();
            }

            if (!forceRefresh && await TryLoadFromDiskAsync(profile, ct).ConfigureAwait(false))
            {
                var age = DateTimeOffset.UtcNow - (Current.LoadedAt ?? DateTimeOffset.MinValue);

                if (age < Ttl)
                {
                    log.LogInformation(
                        "Catalogs served from cache, {Age:F1}h old (live={Live}, vod={Vod}, series={Series}).",
                        age.TotalHours, Current.Live.Items.Count, Current.Vod.Items.Count,
                        Current.Series.Items.Count);
                    return;
                }

                log.LogInformation("Cached catalogs are {Age:F1}h old; refreshing.", age.TotalHours);
            }

            await RefreshAsync(profile, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RefreshAsync(Profile profile, CancellationToken ct)
    {
        var client = clients.For(profile);

        // Sequential, not parallel. Providers cap concurrent connections
        // (commonly 1 or 2), and three simultaneous multi-megabyte downloads can
        // exhaust the limit and break playback that is already running.
        var live = await FetchAsync(CatalogKind.Live, "live channels",
            () => client.GetLiveCatalogAsync(ct), Current.Live).ConfigureAwait(false);

        var vod = await FetchAsync(CatalogKind.Vod, "movies",
            () => client.GetVodCatalogAsync(ct), Current.Vod).ConfigureAwait(false);

        var series = await FetchAsync(CatalogKind.Series, "series",
            () => client.GetSeriesCatalogAsync(ct), Current.Series).ConfigureAwait(false);

        Current = new CatalogState
        {
            Live = live,
            Vod = vod,
            Series = series,
            ProfileId = profile.Id,
            LoadedAt = DateTimeOffset.UtcNow,
        };

        Changed?.Invoke(this, Current);

        await SaveToDiskAsync(profile, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches one catalog, keeping the previous copy if the provider fails.
    /// </summary>
    /// <remarks>
    /// One failing catalog must not take the other two down. A provider with no
    /// series, or a temporarily broken endpoint, should still leave live and VOD
    /// usable.
    /// </remarks>
    private async Task<Catalog<T>> FetchAsync<T>(
        CatalogKind kind, string label, Func<Task<Catalog<T>>> fetch, Catalog<T> previous)
    {
        Progress?.Invoke(this, new CatalogProgress { Kind = kind, Message = $"Loading {label}..." });

        try
        {
            var result = await fetch().ConfigureAwait(false);

            log.LogInformation("Loaded {Count} {Label} in {Categories} categories.",
                result.Items.Count, label, result.Categories.Count);

            Progress?.Invoke(this, new CatalogProgress
            {
                Kind = kind,
                Message = $"{result.Items.Count:N0} {label}",
                IsComplete = true,
            });

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not load {Label}; keeping the previous copy.", label);

            Progress?.Invoke(this, new CatalogProgress
            {
                Kind = kind,
                Message = $"{label} unavailable: {ex.Message}",
                IsComplete = true,
            });

            return previous;
        }
    }

    // ---- detail caches --------------------------------------------------

    public async Task<SeriesDetail?> GetSeriesDetailAsync(string seriesId, CancellationToken ct = default)
    {
        if (_seriesDetails.TryGetValue(seriesId, out var cached))
        {
            return cached;
        }

        if (_profile is null)
        {
            return null;
        }

        var detail = await clients.For(_profile).GetSeriesDetailAsync(seriesId, ct).ConfigureAwait(false);

        if (detail is not null)
        {
            _seriesDetails[seriesId] = detail;
        }

        return detail;
    }

    public async Task<VodDetail?> GetVodDetailAsync(string vodId, CancellationToken ct = default)
    {
        if (_vodDetails.TryGetValue(vodId, out var cached))
        {
            return cached;
        }

        if (_profile is null)
        {
            return null;
        }

        var detail = await clients.For(_profile).GetVodDetailAsync(vodId, ct).ConfigureAwait(false);

        if (detail is not null)
        {
            _vodDetails[vodId] = detail;
        }

        return detail;
    }

    public void Clear()
    {
        Current = new CatalogState();
        _seriesDetails.Clear();
        _vodDetails.Clear();
        _profile = null;
        Changed?.Invoke(this, Current);
    }

    // ---- disk cache -----------------------------------------------------

    private static string CacheDirectory(Profile profile) =>
        Path.Combine(DatabasePaths.CacheDirectory, profile.Id);

    private static string CacheFile(Profile profile, CatalogKind kind) =>
        Path.Combine(CacheDirectory(profile), $"{kind.ToString().ToLowerInvariant()}.json.gz");

    private async Task<bool> TryLoadFromDiskAsync(Profile profile, CancellationToken ct)
    {
        try
        {
            var live = await ReadAsync<Catalog<LiveChannel>>(CacheFile(profile, CatalogKind.Live), ct)
                .ConfigureAwait(false);
            var vod = await ReadAsync<Catalog<VodItem>>(CacheFile(profile, CatalogKind.Vod), ct)
                .ConfigureAwait(false);
            var series = await ReadAsync<Catalog<SeriesItem>>(CacheFile(profile, CatalogKind.Series), ct)
                .ConfigureAwait(false);

            if (live is null && vod is null && series is null)
            {
                return false;
            }

            Current = new CatalogState
            {
                Live = live ?? Catalog<LiveChannel>.Empty,
                Vod = vod ?? Catalog<VodItem>.Empty,
                Series = series ?? Catalog<SeriesItem>.Empty,
                ProfileId = profile.Id,
                // The oldest snapshot decides staleness.
                LoadedAt = new[] { live?.LoadedAt, vod?.LoadedAt, series?.LoadedAt }
                    .Where(d => d is not null)
                    .Min(),
            };

            Changed?.Invoke(this, Current);
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            // A corrupt or half-written cache is never fatal: refetch instead.
            log.LogWarning(ex, "The catalog cache could not be read; refetching.");
            return false;
        }
    }

    private static async Task<T?> ReadAsync<T>(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        await using var file = File.OpenRead(path);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);

        return await JsonSerializer.DeserializeAsync<T>(gzip, CacheJson, ct).ConfigureAwait(false);
    }

    private async Task SaveToDiskAsync(Profile profile, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory(profile));

            await WriteAsync(CacheFile(profile, CatalogKind.Live), Current.Live, ct).ConfigureAwait(false);
            await WriteAsync(CacheFile(profile, CatalogKind.Vod), Current.Vod, ct).ConfigureAwait(false);
            await WriteAsync(CacheFile(profile, CatalogKind.Series), Current.Series, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cache is an optimization; failing to write it is not an error.
            log.LogWarning(ex, "Could not write the catalog cache.");
        }
    }

    private static async Task WriteAsync<T>(string path, T value, CancellationToken ct)
    {
        // Written to a temp file and moved into place, so an interrupted write
        // cannot leave a half-parsed cache behind.
        var temp = path + ".tmp";

        await using (var file = File.Create(temp))
        await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        {
            await JsonSerializer.SerializeAsync(gzip, value, CacheJson, ct).ConfigureAwait(false);
        }

        File.Move(temp, path, overwrite: true);
    }
}
