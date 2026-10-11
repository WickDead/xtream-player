using XtreamPlayer.Core.Library;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Infrastructure.Xtream;
using XtreamPlayer.Infrastructure.Epg;

namespace XtreamPlayer.Application;

/// <summary>Browser-safe provider access. Uses the same Xtream client and XMLTV parser as desktop.</summary>
public sealed class DirectProviderClient(HttpClient http, IDeviceStorage storage) : IRemotePlayback
{
    public const string StoragePrefix = "direct:v1";
    private List<Profile> _profiles = [];
    private readonly Dictionary<string, XtreamClient> _clients = [];
    private readonly Dictionary<string, Catalog<LiveChannel>> _live = [];
    private readonly Dictionary<string, Catalog<VodItem>> _vod = [];
    private readonly Dictionary<string, Catalog<SeriesItem>> _series = [];
    private readonly Dictionary<string, SeriesDetail> _details = [];
    public string CachePrefix => StoragePrefix;
    public async Task Initialize() => _profiles = await storage.GetAsync<List<Profile>>(StoragePrefix + ":profiles") ?? [];
    public List<ProviderSummary> Providers() => _profiles.Select(p => new ProviderSummary(p.Id, p.Name, p.Status ?? "Saved", p.MaxConnections)).ToList();
    public static string BrowserUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
            !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Enter a valid HTTP(S) provider address.");
        if (uri.Scheme != "https" && !uri.IsLoopback)
            throw new InvalidOperationException("Use your provider's HTTPS address. The hosted app cannot load HTTP-only providers or streams.");
        return uri.AbsoluteUri;
    }
    private XtreamClient Client(string id)
    {
        if (_clients.TryGetValue(id, out var client)) return client;
        var profile = _profiles.FirstOrDefault(p => p.Id == id) ?? throw new InvalidOperationException("Add or select a provider first.");
        return _clients[id] = new(http, profile);
    }
    private static async Task<T> ProviderCall<T>(Func<Task<T>> call)
    {
        try { return await call(); }
        catch (XtreamException e) when (e.IsAuthFailure) { throw new InvalidOperationException("The provider rejected this login or its connection limit was reached."); }
        catch (XtreamException) { throw new InvalidOperationException("Could not read the provider response. Check the login and HTTPS address. The provider must allow browser access (CORS)."); }
        catch (HttpRequestException) { throw new InvalidOperationException("Could not reach the provider. Check its HTTPS address and browser access (CORS)."); }
    }
    public async Task<ProviderSummary> Add(SaveProvider request)
    {
        var url = ServerUrl.Normalize(request.ServerUrl);
        BrowserUrl(url);
        var parsed = new Uri(url);
        if (parsed.Query.Length > 0 || parsed.Fragment.Length > 0)
            throw new InvalidOperationException("Enter the provider's base address, without a playlist path or login query.");
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password))
            throw new InvalidOperationException("Name, username and password are required.");
        var profile = new Profile { Id = Guid.NewGuid().ToString("N"), Name = request.Name.Trim(), ServerUrl = url,
            Username = request.Username.Trim(), Password = request.Password, PreferredLiveFormat = LiveStreamFormat.Hls,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var client = new XtreamClient(http, profile);
        var account = await ProviderCall(() => client.AuthenticateAsync());
        profile = profile with { Status = account.Status, MaxConnections = account.MaxConnections };
        var next = _profiles.Append(profile).ToList();
        await storage.SetAsync(StoragePrefix + ":profiles", next);
        _profiles = next; _clients[profile.Id] = client;
        return new(profile.Id, profile.Name, account.Status, account.MaxConnections);
    }
    public async Task Remove(string id)
    {
        var next = _profiles.Where(p => p.Id != id).ToList();
        await storage.SetAsync(StoragePrefix + ":profiles", next);
        _profiles = next; _clients.Remove(id); _live.Remove(id); _vod.Remove(id); _series.Remove(id);
        foreach (var key in _details.Keys.Where(k => k.StartsWith(id + ":")).ToList()) _details.Remove(key);
    }
    private async Task<Catalog<LiveChannel>> Live(string p, bool refresh = false) =>
        !refresh && _live.TryGetValue(p, out var cached) ? cached : _live[p] = await ProviderCall(() => Client(p).GetLiveCatalogAsync());
    private async Task<Catalog<VodItem>> Vod(string p, bool refresh = false) =>
        !refresh && _vod.TryGetValue(p, out var cached) ? cached : _vod[p] = await ProviderCall(() => Client(p).GetVodCatalogAsync());
    private async Task<Catalog<SeriesItem>> Series(string p, bool refresh = false) =>
        !refresh && _series.TryGetValue(p, out var cached) ? cached : _series[p] = await ProviderCall(() => Client(p).GetSeriesCatalogAsync());
    private async Task<SeriesDetail> Episodes(string p, string id)
    {
        var key = p + ":" + id;
        if (_details.TryGetValue(key, out var detail)) return detail;
        return _details[key] = await ProviderCall(() => Client(p).GetSeriesDetailAsync(id)) ?? throw new InvalidOperationException("Series details are unavailable.");
    }
    private static string? Image(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0 ? uri.AbsoluteUri : null;
    public async Task<BrowseCatalog> Browse(string p, string kind, bool refresh = false)
    {
        if (kind == "live")
        {
            var c = await Live(p, refresh);
            return new(c.Items.Select(x => new MediaItem(x.Id, x.Name, x.CategoryId, x.CategoryOrder, kind, Image(x.IconUrl),
                HasArchive: x.HasArchive, ArchiveDays: x.ArchiveDays)).ToList(), c.Categories.ToList(), c.LoadedAt);
        }
        if (kind == "vod")
        {
            var c = await Vod(p, refresh);
            return new(c.Items.Select(x => new MediaItem(x.Id, x.Name, x.CategoryId, x.CategoryOrder, kind, Image(x.PosterUrl), x.Added)).ToList(), c.Categories.ToList(), c.LoadedAt);
        }
        if (kind == "series")
        {
            var c = await Series(p, refresh);
            return new(c.Items.Select(x => new MediaItem(x.Id, x.Name, x.CategoryId, x.CategoryOrder, kind, Image(x.PosterUrl), x.LastModified)).ToList(), c.Categories.ToList(), c.LoadedAt);
        }
        throw new InvalidOperationException("Choose live TV, movies or series.");
    }
    public async Task<MediaDetail> Detail(string p, string kind, string id)
    {
        if (kind == "series")
        {
            var d = await Episodes(p, id);
            return new(d.Name, d.Plot, d.Genre, d.Rating, d.EpisodesBySeason.OrderBy(x => x.Key).SelectMany(x => x.Value)
                .Select(e => new MediaItem(e.Id, $"S{e.SeasonNumber:00} E{e.EpisodeNumber:00} · {e.Title}", e.SeasonNumber.ToString(),
                    e.SeasonNumber, "episode", Image(e.StillUrl), e.Added, id)).ToList());
        }
        var v = await ProviderCall(() => Client(p).GetVodDetailAsync(id)) ?? throw new InvalidOperationException("Movie details are unavailable.");
        return new(v.Name, v.Plot, v.Genre, v.Rating, []);
    }
    public async Task<PlaybackSession> OpenAsync(PlayRequest r, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string url;
        if (r.Kind == "live")
        {
            var item = (await Live(r.ProviderId)).Items.First(x => x.Id == r.ContentId);
            if (r.Start is { } start)
            {
                if (!item.HasArchive || start < DateTimeOffset.UtcNow.AddDays(-item.ArchiveDays) ||
                    start >= DateTimeOffset.UtcNow || r.DurationMinutes is not (> 0 and <= 1440))
                    throw new InvalidOperationException("This programme is outside the catch-up window.");
                // The provider's standard archive endpoint may be TS-only. Browser support is provider-dependent.
                url = Client(r.ProviderId).Urls.Timeshift(item.Id, r.DurationMinutes.Value, start);
            }
            else url = item.StreamUrl;
        }
        else if (r.Kind == "vod") url = (await Vod(r.ProviderId)).Items.First(x => x.Id == r.ContentId).StreamUrl;
        else if (r.Kind == "episode" && r.SeriesId is not null)
            url = (await Episodes(r.ProviderId, r.SeriesId)).EpisodesBySeason.Values.SelectMany(x => x).First(x => x.Id == r.ContentId).StreamUrl;
        else throw new InvalidOperationException("Choose a playable title.");
        return new("direct", BrowserUrl(url), 0, 0, [], new(r.Kind != "live" || r.Start is not null, false, false, true, false));
    }
    public Task CloseAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<EpgEntry>> ShortGuide(string p, string id) => ProviderCall(() => Client(p).GetShortEpgAsync(id));
    public async Task<Dictionary<string, List<EpgProgramme>>> Guide(string p, DateTimeOffset from, DateTimeOffset to, bool names)
    {
        var channels = new List<EpgChannel>();
        var programmes = new List<EpgProgramme>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var response = await http.GetAsync(Client(p).Urls.Xmltv(), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        await new XmltvParser { PastWindow = TimeSpan.FromDays(7), FutureWindow = TimeSpan.FromDays(2) }.ParseAsync(stream,
            (c, _) => { channels.Add(c); return Task.CompletedTask; },
            (e, _) => { if (e.Stop > from && e.Start < to) { if (programmes.Count >= 250000) throw new InvalidOperationException("Guide is too large for this browser."); programmes.Add(e); } return Task.CompletedTask; }, timeout.Token);
        var live = (await Live(p)).Items;
        var mappings = EpgChannelMatcher.Match(live.Select(c => new EpgChannelMatcher.Input(c.Id, c.Name, c.EpgChannelId)).ToList(),
            channels.Select(c => new EpgChannelMatcher.Candidate(c.XmltvId, c.DisplayName)).ToList());
        var byId = programmes.GroupBy(e => e.XmltvChannelId).ToDictionary(g => g.Key, g => g.ToList());
        var guide = mappings.Where(m => m.XmltvChannelId is not null && byId.ContainsKey(m.XmltvChannelId))
            .ToDictionary(m => m.ChannelId, m => EpgTimeline.Normalize(byId[m.XmltvChannelId!]));
        if (names) foreach (var channel in live)
            if (!guide.TryGetValue(channel.Id, out var entries) || entries.Count == 0)
                guide[channel.Id] = ChannelNameSchedule.Programmes(channel, from, to, DateTimeOffset.UtcNow).ToList();
        return guide;
    }
}
