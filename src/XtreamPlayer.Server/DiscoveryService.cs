using XtreamPlayer.Application;
using XtreamPlayer.Core.Library;
using XtreamPlayer.Infrastructure.Sports;
using XtreamPlayer.Infrastructure.TvMaze;

namespace XtreamPlayer.Server;

public sealed class DiscoveryService(CatalogService catalogs, IAiringScheduleService schedule)
{
    public Task<DiscoveryResult> Get(string provider, bool refresh) => catalogs.Cached(provider + ":discovery", async () =>
    {
        await schedule.RefreshIfStaleAsync();
        var series = await catalogs.Series(provider);
        var candidates = NewEpisodeMatcher.FindCandidates(series.Items, schedule.Current, DateTimeOffset.UtcNow, TimeSpan.FromDays(7));
        var found = new List<string>();
        foreach (var candidate in candidates)
        {
            var detail = await catalogs.SeriesDetail(provider, candidate.Key);
            if (detail is null) continue;
            var episodes = detail.EpisodesBySeason.Values.SelectMany(x => x)
                .Select(e => new XtreamPlayer.Core.Models.ProviderEpisode(e.SeasonNumber, e.EpisodeNumber, e.Added)).ToList();
            if (NewEpisodeMatcher.FindAvailable(candidate.Value, episodes) is not null) found.Add(candidate.Key);
        }
        return new DiscoveryResult(found);
    }, refresh);
}
public sealed class ServerSportsKey(ServerStore store, IHttpClientFactory clients) : ISportsDbKeyStore
{
    public Task<string?> GetAsync(CancellationToken ct = default) => Task.FromResult(store.Get<string>("setting", "sports-key"));
    public Task SetAsync(string? key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) store.Delete("setting", "sports-key");
        else store.Put("setting", "sports-key", key);
        return Task.CompletedTask;
    }
    public async Task<int?> TestAsync(string key, CancellationToken ct = default)
    {
        try { return (await new TheSportsDbClient(clients.CreateClient(TheSportsDbClient.HttpClientName)).GetDayAsync(key, DateOnly.FromDateTime(DateTime.UtcNow), ct)).Count; }
        catch (SportsDbKeyRejectedException) { return null; }
    }
}
