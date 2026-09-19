using System.Globalization;
using System.Net;
using System.Text.Json;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Infrastructure.Xtream.Json;

namespace XtreamPlayer.Infrastructure.Xtream;

public sealed class XtreamException(string message, HttpStatusCode? status = null, Exception? inner = null)
    : Exception(message, inner)
{
    public HttpStatusCode? Status { get; } = status;

    /// <summary>True when the provider rejected the credentials or the connection slot.</summary>
    public bool IsAuthFailure => Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}

/// <summary>
/// Talks to one Xtream provider.
/// </summary>
/// <remarks>
/// <para>
/// One instance per profile, cached and reused. The old client constructed a
/// fresh client and re-read the credentials out of SQLite on literally every
/// call, which is a meaningful part of why the old app felt slow.
/// </para>
/// <para>
/// Every catalog response is deserialized straight from the network stream.
/// <c>get_vod_streams</c> measured 26.1 MB across 68,595 items on a real
/// provider; buffering that through a string would double it on the large object
/// heap for no benefit.
/// </para>
/// </remarks>
public sealed class XtreamClient(HttpClient http, Profile profile)
{
    private static readonly TimeSpan SmallCallTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CatalogTimeout = TimeSpan.FromMinutes(3);

    private readonly XtreamUrlBuilder _urls =
        new(profile.ServerUrl, profile.Username, profile.Password);

    private AccountInfo? _account;

    public Profile Profile { get; } = profile;

    public XtreamUrlBuilder Urls => _urls;

    /// <summary>Cached for the session; refreshed by an explicit sync.</summary>
    public AccountInfo? Account => _account;

    // ---- authentication -------------------------------------------------

    public async Task<AccountInfo> AuthenticateAsync(bool force = false, CancellationToken ct = default)
    {
        if (_account is not null && !force)
        {
            return _account;
        }

        var raw = await GetAsync<RawAuthResponse>(_urls.Api(), SmallCallTimeout, ct)
            ?? throw new XtreamException("The provider returned an empty authentication response.");

        var info = raw.UserInfo;

        if (info is null)
        {
            throw new XtreamException(
                "The provider did not return account information. The server URL may be wrong.");
        }

        var status = info.status ?? "Unknown";

        if (!string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase))
        {
            // Surface the provider's own message; it usually says why.
            throw new XtreamException(
                $"The account is not active (status: {status}). {info.message}".TrimEnd());
        }

        _account = new AccountInfo
        {
            Status = status,
            Message = info.message,
            ExpiresAt = long.TryParse(info.ExpDate, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var exp) && exp > 0
                ? DateTimeOffset.FromUnixTimeSeconds(exp)
                : null,
            MaxConnections = info.MaxConnections is > 0 ? (int)info.MaxConnections.Value : null,
            ActiveConnections = info.ActiveCons is > 0 ? (int)info.ActiveCons.Value : 0,
            IsTrial = info.IsTrial == "1",
            AllowedOutputFormats = info.AllowedOutputFormats ?? [],
            ServerUrl = raw.ServerInfo?.url,
            ServerPort = raw.ServerInfo?.port,
            TimeZone = raw.ServerInfo?.timezone,
        };

        return _account;
    }

    // ---- live -----------------------------------------------------------

    public async Task<Catalog<LiveChannel>> GetLiveCatalogAsync(CancellationToken ct = default)
    {
        var account = await AuthenticateAsync(ct: ct).ConfigureAwait(false);

        var extension = XtreamUrlBuilder.ChooseLiveExtension(
            account.AllowedOutputFormats, Profile.PreferredLiveFormat);

        var categories = await GetCategoriesAsync("get_live_categories", ct).ConfigureAwait(false);
        var byId = categories.ToDictionary(c => c.Id, StringComparer.Ordinal);

        var raw = await GetListAsync<RawLiveStream>(
            _urls.Api("get_live_streams"), CatalogTimeout, ct).ConfigureAwait(false);

        var channels = new List<LiveChannel>(raw.Count);

        foreach (var item in raw)
        {
            if (string.IsNullOrWhiteSpace(item.StreamId))
            {
                continue;
            }

            var categoryId = item.CategoryId ?? string.Empty;
            byId.TryGetValue(categoryId, out var category);

            channels.Add(new LiveChannel
            {
                Id = item.StreamId,
                Name = item.name?.Trim() ?? $"Channel {item.StreamId}",
                CategoryId = categoryId,
                CategoryName = category?.Name,
                CategoryOrder = category?.Order ?? int.MaxValue,
                EpgChannelId = item.EpgChannelId,
                IconUrl = NormalizeImageUrl(item.StreamIcon),
                Number = int.TryParse(item.num, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var n) ? n : null,
                StreamUrl = _urls.Live(item.StreamId, extension),
                HasArchive = item.TvArchive is > 0,
                ArchiveDays = (int)(item.TvArchiveDuration ?? 0),
            });
        }

        CountItems(categories, channels, c => c.CategoryId);

        return new Catalog<LiveChannel> { Items = channels, Categories = categories };
    }

    // ---- VOD ------------------------------------------------------------

    public async Task<Catalog<VodItem>> GetVodCatalogAsync(CancellationToken ct = default)
    {
        var categories = await GetCategoriesAsync("get_vod_categories", ct).ConfigureAwait(false);
        var byId = categories.ToDictionary(c => c.Id, StringComparer.Ordinal);

        var raw = await GetListAsync<RawVodStream>(
            _urls.Api("get_vod_streams"), CatalogTimeout, ct).ConfigureAwait(false);

        var items = new List<VodItem>(raw.Count);

        foreach (var item in raw)
        {
            if (string.IsNullOrWhiteSpace(item.StreamId))
            {
                continue;
            }

            var categoryId = item.CategoryId ?? string.Empty;
            byId.TryGetValue(categoryId, out var category);
            var container = item.ContainerExtension ?? "mp4";

            items.Add(new VodItem
            {
                Id = item.StreamId,
                Name = item.name?.Trim() ?? $"Movie {item.StreamId}",
                CategoryId = categoryId,
                CategoryName = category?.Name,
                CategoryOrder = category?.Order ?? int.MaxValue,
                PosterUrl = NormalizeImageUrl(item.StreamIcon),
                Rating = XtreamText.NormalizeRating(item.rating),
                Added = item.added,
                ContainerExtension = container,
                StreamUrl = _urls.Movie(item.StreamId, container),
            });
        }

        CountItems(categories, items, i => i.CategoryId);

        return new Catalog<VodItem> { Items = items, Categories = categories };
    }

    public async Task<VodDetail?> GetVodDetailAsync(string vodId, CancellationToken ct = default)
    {
        var raw = await GetAsync<RawVodInfoResponse>(
            _urls.Api("get_vod_info", ("vod_id", vodId)), SmallCallTimeout, ct).ConfigureAwait(false);

        if (raw?.info is null && raw?.MovieData is null)
        {
            return null;
        }

        var info = raw.info;
        var quality = info?.Video is { width: > 0, height: > 0 } v
            ? $"{v.width}x{v.height}"
            : null;

        return new VodDetail
        {
            Id = vodId,
            Name = raw.MovieData?.name?.Trim() ?? "Unknown",
            Plot = info?.plot,
            Cast = info?.cast,
            Director = info?.director,
            Genre = info?.genre,
            ReleaseDate = info?.ReleaseDate,
            Rating = XtreamText.NormalizeRating(info?.rating),
            DurationLabel = info?.duration,
            PosterUrl = NormalizeImageUrl(info?.MovieImage),
            BackdropUrl = NormalizeImageUrl(info?.BackdropPath?.FirstOrDefault()),
            YoutubeTrailer = info?.YoutubeTrailer,
            Quality = quality,
        };
    }

    // ---- series ---------------------------------------------------------

    public async Task<Catalog<SeriesItem>> GetSeriesCatalogAsync(CancellationToken ct = default)
    {
        var categories = await GetCategoriesAsync("get_series_categories", ct).ConfigureAwait(false);
        var byId = categories.ToDictionary(c => c.Id, StringComparer.Ordinal);

        var raw = await GetListAsync<RawSeries>(
            _urls.Api("get_series"), CatalogTimeout, ct).ConfigureAwait(false);

        var items = new List<SeriesItem>(raw.Count);

        foreach (var item in raw)
        {
            if (string.IsNullOrWhiteSpace(item.SeriesId))
            {
                continue;
            }

            var categoryId = item.CategoryId ?? string.Empty;
            byId.TryGetValue(categoryId, out var category);

            items.Add(new SeriesItem
            {
                Id = item.SeriesId,
                Name = item.name?.Trim() ?? $"Series {item.SeriesId}",
                CategoryId = categoryId,
                CategoryName = category?.Name,
                CategoryOrder = category?.Order ?? int.MaxValue,
                PosterUrl = NormalizeImageUrl(item.cover),
                Plot = item.plot,
                Genre = item.genre,
                Rating = XtreamText.NormalizeRating(item.rating),
                LastModified = item.LastModified,
            });
        }

        CountItems(categories, items, i => i.CategoryId);

        return new Catalog<SeriesItem> { Items = items, Categories = categories };
    }

    public async Task<SeriesDetail?> GetSeriesDetailAsync(string seriesId, CancellationToken ct = default)
    {
        var raw = await GetAsync<RawSeriesInfoResponse>(
            _urls.Api("get_series_info", ("series_id", seriesId)), SmallCallTimeout, ct)
            .ConfigureAwait(false);

        if (raw is null)
        {
            return null;
        }

        var episodesBySeason = new Dictionary<int, IReadOnlyList<SeriesEpisode>>();

        foreach (var (key, list) in raw.episodes)
        {
            if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seasonNumber))
            {
                continue;
            }

            var episodes = new List<SeriesEpisode>(list.Count);

            foreach (var episode in list)
            {
                if (string.IsNullOrWhiteSpace(episode.id))
                {
                    continue;
                }

                var number = (int)(episode.EpisodeNum ?? 0);

                episodes.Add(new SeriesEpisode
                {
                    Id = episode.id,
                    Title = string.IsNullOrWhiteSpace(episode.title)
                        ? $"Episode {number}"
                        : episode.title.Trim(),
                    SeasonNumber = (int)(episode.season ?? seasonNumber),
                    EpisodeNumber = number,
                    Plot = episode.info?.plot,
                    StillUrl = NormalizeImageUrl(episode.info?.MovieImage),
                    DurationLabel = episode.info?.duration,
                    ContainerExtension = episode.ContainerExtension ?? "mp4",
                    StreamUrl = _urls.Episode(episode.id, episode.ContainerExtension, episode.DirectSource),
                    Added = episode.added,
                });
            }

            episodes.Sort((a, b) => a.EpisodeNumber.CompareTo(b.EpisodeNumber));
            episodesBySeason[seasonNumber] = episodes;
        }

        var seasons = BuildSeasons(raw.seasons, episodesBySeason);

        return new SeriesDetail
        {
            SeriesId = seriesId,
            Name = raw.info?.name?.Trim() ?? "Unknown",
            Plot = raw.info?.plot,
            Cast = raw.info?.cast,
            Genre = raw.info?.genre,
            PosterUrl = NormalizeImageUrl(raw.info?.cover),
            BackdropUrl = NormalizeImageUrl(raw.info?.BackdropPath?.FirstOrDefault()),
            Rating = XtreamText.NormalizeRating(raw.info?.rating),
            Seasons = seasons,
            EpisodesBySeason = episodesBySeason,
        };
    }

    /// <summary>
    /// Reconciles the declared season list against the episode map.
    /// </summary>
    /// <remarks>
    /// Ports the defensive fix at xtream.rs:464-486, which is a genuine
    /// real-world necessity rather than paranoia. Plenty of providers return an
    /// empty <c>seasons</c> array while <c>episodes</c> is fully populated; the
    /// naive reading is a series that appears to have no content at all. The
    /// reverse also happens, so declared seasons with no episodes get an empty
    /// list rather than a missing key.
    /// </remarks>
    internal static IReadOnlyList<SeriesSeason> BuildSeasons(
        List<RawSeason> declared,
        Dictionary<int, IReadOnlyList<SeriesEpisode>> episodesBySeason)
    {
        var seasons = new Dictionary<int, SeriesSeason>();

        foreach (var season in declared)
        {
            if (season.SeasonNumber is null)
            {
                continue;
            }

            var number = (int)season.SeasonNumber.Value;

            seasons[number] = new SeriesSeason
            {
                Number = number,
                Name = string.IsNullOrWhiteSpace(season.name) ? $"Season {number}" : season.name.Trim(),
                Overview = season.overview,
                PosterUrl = NormalizeImageUrl(season.cover),
                EpisodeCount = episodesBySeason.TryGetValue(number, out var known)
                    ? known.Count
                    : (int)(season.EpisodeCount ?? 0),
            };
        }

        // Seasons present only in the episode map.
        foreach (var (number, episodes) in episodesBySeason)
        {
            if (seasons.ContainsKey(number))
            {
                continue;
            }

            seasons[number] = new SeriesSeason
            {
                Number = number,
                Name = number == 0 ? "Specials" : $"Season {number}",
                EpisodeCount = episodes.Count,
            };
        }

        return [.. seasons.Values.OrderBy(s => s.Number)];
    }

    // ---- EPG ------------------------------------------------------------

    public async Task<IReadOnlyList<EpgEntry>> GetShortEpgAsync(
        string streamId, int limit = 8, CancellationToken ct = default)
    {
        var raw = await GetAsync<RawShortEpgResponse>(
            _urls.Api("get_short_epg",
                ("stream_id", streamId),
                ("limit", limit.ToString(CultureInfo.InvariantCulture))),
            SmallCallTimeout, ct).ConfigureAwait(false);

        if (raw is null)
        {
            return [];
        }

        // Providers report EPG times in the server's zone, not UTC.
        var offset = ServerOffset();
        var entries = new List<EpgEntry>(raw.EpgListings.Count);

        foreach (var listing in raw.EpgListings)
        {
            var start = XtreamText.ParseEpgTimestamp(listing.StartTimestamp, offset)
                        ?? XtreamText.ParseEpgTimestamp(listing.start, offset);

            var stop = XtreamText.ParseEpgTimestamp(listing.StopTimestamp, offset)
                       ?? XtreamText.ParseEpgTimestamp(listing.end, offset);

            if (start is null || stop is null)
            {
                continue;
            }

            entries.Add(new EpgEntry
            {
                ChannelId = streamId,
                Start = start.Value,
                Stop = stop.Value,
                // Providers base64-encode these; DecodeEpgText falls back safely.
                Title = XtreamText.DecodeEpgText(listing.title),
                Description = string.IsNullOrWhiteSpace(listing.description)
                    ? null
                    : XtreamText.DecodeEpgText(listing.description),
            });
        }

        entries.Sort((a, b) => a.Start.CompareTo(b.Start));
        return entries;
    }

    private TimeSpan ServerOffset()
    {
        var zone = _account?.TimeZone;

        if (string.IsNullOrWhiteSpace(zone))
        {
            return TimeSpan.Zero;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(zone).GetUtcOffset(DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeSpan.Zero;
        }
    }

    // ---- plumbing -------------------------------------------------------

    private async Task<IReadOnlyList<Category>> GetCategoriesAsync(string action, CancellationToken ct)
    {
        var raw = await GetListAsync<RawCategory>(_urls.Api(action), SmallCallTimeout, ct)
            .ConfigureAwait(false);

        var categories = new List<Category>(raw.Count);
        var order = 0;

        foreach (var item in raw)
        {
            if (string.IsNullOrWhiteSpace(item.CategoryId))
            {
                continue;
            }

            // The provider's own ordering is preserved as an index; it is what
            // the "API order" sort uses, and it is frequently more meaningful
            // than alphabetical.
            categories.Add(new Category
            {
                Id = item.CategoryId,
                Name = item.CategoryName?.Trim() ?? item.CategoryId,
                Order = order++,
            });
        }

        return categories;
    }

    private static void CountItems<T>(
        IReadOnlyList<Category> categories, IReadOnlyList<T> items, Func<T, string> categoryOf)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            var key = categoryOf(item);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        foreach (var category in categories)
        {
            category.ItemCount = counts.GetValueOrDefault(category.Id);
        }
    }

    private async Task<T?> GetAsync<T>(string url, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);

        try
        {
            using var response = await http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token)
                .ConfigureAwait(false);

            EnsureSuccess(response);

            await using var stream = await response.Content
                .ReadAsStreamAsync(timeoutSource.Token).ConfigureAwait(false);

            return await JsonSerializer
                .DeserializeAsync<T>(stream, XtreamJson.Options, timeoutSource.Token)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new XtreamException(
                "The provider returned a response that could not be read as JSON. "
              + "This usually means the server URL points at a web page rather than an Xtream API.",
                inner: ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new XtreamException($"The provider did not respond within {timeout.TotalSeconds:F0}s.");
        }
        catch (HttpRequestException ex)
        {
            throw new XtreamException($"Could not reach the provider: {ex.Message}", inner: ex);
        }
    }

    /// <summary>
    /// Reads a catalog array, tolerating a provider that returns an object or
    /// null instead (which happens when an account is blocked).
    /// </summary>
    private async Task<List<T>> GetListAsync<T>(string url, TimeSpan timeout, CancellationToken ct) =>
        await GetAsync<List<T>>(url, timeout, ct).ConfigureAwait(false) ?? [];

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "The provider rejected the credentials. Check the username and password, "
              + "or another device may be using your connection slot.",
            HttpStatusCode.NotFound =>
                "The provider returned 404. The server URL is probably wrong.",
            HttpStatusCode.ProxyAuthenticationRequired =>
                "The provider's edge server refused the request (407). "
              + "This content may no longer be available on your plan.",
            _ => $"The provider returned {(int)response.StatusCode} {response.ReasonPhrase}.",
        };

        throw new XtreamException(message, response.StatusCode);
    }

    /// <summary>
    /// Providers send "", "null" and relative paths for images.
    /// </summary>
    private static string? NormalizeImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var trimmed = url.Trim();

        if (trimmed is "null" or "N/A" or "0")
        {
            return null;
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed)
            && parsed.Scheme is "http" or "https"
            ? trimmed
            : null;
    }
}
