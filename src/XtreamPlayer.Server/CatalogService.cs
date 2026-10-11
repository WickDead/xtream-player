using System.Collections.Concurrent;
using XtreamPlayer.Application;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Core.Library;
using XtreamPlayer.Infrastructure.Xtream;
using XtreamPlayer.Infrastructure.Epg;

namespace XtreamPlayer.Server;

public sealed class CatalogService(ServerStore store, IHttpClientFactory http)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    public XtreamClient Client(string id) => new(http.CreateClient("upstream"), store.Profile(id));
    public async Task<T> Cached<T>(string key, Func<Task<T>> fetch, bool refresh = false)
    {
        var gate = _locks.GetOrAdd(key, _ => new(1));
        await gate.WaitAsync();
        try
        {
            var cached = store.Get<Snapshot<T>>("cache", key);
            if (!refresh && cached is not null && cached.At > DateTimeOffset.UtcNow.AddHours(-12)) return cached.Value;
            try { var value = await fetch(); store.Put("cache", key, new Snapshot<T>(DateTimeOffset.UtcNow, value)); return value; }
            catch when (cached is not null) { return cached.Value; }
        }
        finally { gate.Release(); }
    }
    public Task<Catalog<LiveChannel>> Live(string p, bool refresh = false) => Cached(p + ":live", () => Client(p).GetLiveCatalogAsync(), refresh);
    public Task<Catalog<VodItem>> Vod(string p, bool refresh = false) => Cached(p + ":vod", () => Client(p).GetVodCatalogAsync(), refresh);
    public Task<Catalog<SeriesItem>> Series(string p, bool refresh = false) => Cached(p + ":series", () => Client(p).GetSeriesCatalogAsync(), refresh);
    public Task<SeriesDetail?> SeriesDetail(string p, string id) => Cached(p + ":series:" + id, () => Client(p).GetSeriesDetailAsync(id));
    private readonly ConcurrentDictionary<string, string> _artwork = new();
    private readonly ConcurrentDictionary<string, string> _pendingArtwork = new();
    private void FlushArtwork()
    {
        var batch = new Dictionary<string, string>();
        foreach (var key in _pendingArtwork.Keys)
            if (_pendingArtwork.TryRemove(key, out var url)) batch[key] = url;
        if (batch.Count > 0) store.PutMany("artwork", batch);
    }
    public string? Artwork(string provider, string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        var id = provider + "." + ServerStore.Hash(url!);
        if (_artwork.TryAdd(id, url!)) _pendingArtwork[id] = url!;
        return id;
    }
    public async Task<BrowseCatalog> Browse(string p, string kind, bool refresh = false)
    {
        var result = await BuildBrowse(p, kind, refresh);
        FlushArtwork();
        return result;
    }
    private async Task<BrowseCatalog> BuildBrowse(string p, string kind, bool refresh)
    {
        if (kind == "live")
        {
            var c = await Live(p, refresh);
            return new(c.Items.Select(x => new MediaItem(x.Id, x.Name, x.CategoryId, x.CategoryOrder, kind, Artwork(p, x.IconUrl),
                HasArchive: x.HasArchive, ArchiveDays: x.ArchiveDays)).ToList(), c.Categories.ToList(), c.LoadedAt);
        }
        if (kind == "vod")
        {
            var c = await Vod(p, refresh);
            return new(c.Items.Select(x => new MediaItem(x.Id, x.Name, x.CategoryId, x.CategoryOrder, kind, Artwork(p, x.PosterUrl), x.Added)).ToList(), c.Categories.ToList(), c.LoadedAt);
        }
        if (kind == "series")
        {
            var c = await Series(p, refresh);
            return new(c.Items.Select(x => new MediaItem(x.Id, x.Name, x.CategoryId, x.CategoryOrder, kind, Artwork(p, x.PosterUrl), x.LastModified)).ToList(), c.Categories.ToList(), c.LoadedAt);
        }
        throw new ArgumentException("Unknown catalog.");
    }
    public async Task<MediaDetail> Detail(string p, string kind, string id)
    {
        var result = await BuildDetail(p, kind, id);
        FlushArtwork();
        return result;
    }
    private async Task<MediaDetail> BuildDetail(string p, string kind, string id)
    {
        if (kind == "series")
        {
            var d = await SeriesDetail(p, id) ?? throw new KeyNotFoundException();
            return new(d.Name, d.Plot, d.Genre, d.Rating, d.EpisodesBySeason.OrderBy(x => x.Key).SelectMany(x => x.Value)
                .Select(e => new MediaItem(e.Id, $"S{e.SeasonNumber:00} E{e.EpisodeNumber:00} · {e.Title}",
                    e.SeasonNumber.ToString(), e.SeasonNumber, "episode", Artwork(p, e.StillUrl), e.Added, id)).ToList());
        }
        var v = await Client(p).GetVodDetailAsync(id) ?? throw new KeyNotFoundException();
        return new(v.Name, v.Plot, v.Genre, v.Rating, []);
    }
    public async Task<(string Url, string Title)> Resolve(PlayRequest r)
    {
        if (!double.IsFinite(r.PositionSeconds) || r.PositionSeconds < 0) throw new ArgumentException("Invalid position.");
        if (r.Kind == "live")
        {
            var item = (await Live(r.ProviderId)).Items.First(x => x.Id == r.ContentId);
            if (r.Start is { } start)
            {
                if (!item.HasArchive || start < DateTimeOffset.UtcNow.AddDays(-item.ArchiveDays) ||
                    start >= DateTimeOffset.UtcNow || r.DurationMinutes is not (> 0 and <= 1440))
                    throw new ArgumentException("Programme is outside the catch-up window.");
                return (Client(r.ProviderId).Urls.Timeshift(item.Id, r.DurationMinutes.Value, start), item.Name);
            }
            return (item.StreamUrl, item.Name);
        }
        if (r.Kind == "vod")
        {
            var item = (await Vod(r.ProviderId)).Items.First(x => x.Id == r.ContentId);
            return (item.StreamUrl, item.Name);
        }
        if (r.Kind == "episode" && r.SeriesId is not null)
        {
            var detail = await SeriesDetail(r.ProviderId, r.SeriesId) ?? throw new KeyNotFoundException();
            var e = detail.EpisodesBySeason.Values.SelectMany(x => x).First(x => x.Id == r.ContentId);
            return (e.StreamUrl, $"{detail.Name} · {e.Title}");
        }
        throw new ArgumentException("Choose a playable item.");
    }
    public async Task<Dictionary<string, List<EpgProgramme>>> Guide(string p, DateTimeOffset from, DateTimeOffset to, bool refresh)
    {
        var guide = await Cached(p + ":guide", async () =>
        {
            var channels = new List<EpgChannel>();
            var programmes = new List<EpgProgramme>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            using var response = await http.CreateClient("upstream").GetAsync(Client(p).Urls.Xmltv(), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            await new XmltvParser { PastWindow = TimeSpan.FromDays(7) }.ParseAsync(stream,
                (c, _) => { channels.Add(c); return Task.CompletedTask; },
                (e, _) => { if (programmes.Count > 1000000) throw new InvalidOperationException("Guide is too large."); programmes.Add(e); return Task.CompletedTask; }, timeout.Token);
            var live = (await Live(p)).Items;
            var mappings = EpgChannelMatcher.Match(live.Select(c => new EpgChannelMatcher.Input(c.Id, c.Name, c.EpgChannelId)).ToList(),
                channels.Select(c => new EpgChannelMatcher.Candidate(c.XmltvId, c.DisplayName)).ToList());
            var byId = programmes.GroupBy(e => e.XmltvChannelId).ToDictionary(g => g.Key, g => g.ToList());
            return mappings.Where(m => m.XmltvChannelId is not null && byId.ContainsKey(m.XmltvChannelId))
                .ToDictionary(m => m.ChannelId, m => EpgTimeline.Normalize(byId[m.XmltvChannelId!]));
        }, refresh);
        return guide.ToDictionary(x => x.Key, x => x.Value.Where(e => e.Stop > from && e.Start < to).ToList());
    }
    public sealed record Snapshot<T>(DateTimeOffset At, T Value);
}
