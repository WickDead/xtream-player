using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using XtreamPlayer.Application;
using XtreamPlayer.Core.Library;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Web.Services;

namespace XtreamPlayer.Web;

public partial class App
{
    private static readonly (string Route, string Name, string Icon)[] Links =
    [("home", "Home", "⌂"), ("live", "Live TV", "◉"), ("vod", "Movies", "▰"), ("series", "Series", "▤"),
        ("guide", "TV guide", "▦"), ("settings", "Settings", "⚙")];
    private bool _initialized, _online = true, _busy, _update;
    private string _route = "home", _provider = "", _search = "", _category = "@all", _returnRoute = "vod";
    private string? _message;
    private ItemSort _sort;
    private List<ProviderSummary> _providers = [];
    private BrowseCatalog _catalog = new([], [], default);
    private BrowseWorkflow<MediaItem>? _browse;
    private List<MediaItem> _visible = [];
    private LocalLibrary _library = new();
    private Dictionary<string, List<EpgProgramme>> _guide = [];
    private DateTimeOffset _guideFrom = DateTimeOffset.UtcNow.AddHours(-1);
    private MediaItem? _selected, _playing;
    private MediaDetail? _detail;
    private PlaybackSession? _session;
    private PlayRequest? _request;
    private double _position;
    private TimeSpan? _lastCheckpoint;
    private LocalLibrary _playLibrary = new();
    private string _playLibraryKey = "";
    private string? _pendingVideo;
    private double _pendingPosition;
    private DotNetObjectReference<App>? _reference;
    private string _providerName = "", _providerUrl = "", _username = "", _password = "";
    private string _theme = "dark";
    private bool _nameGuide = true;
    private double? _restoreScroll;
    private string Kind => _route == "guide" ? "live" : _route;
    private string PageTitle => Links.FirstOrDefault(x => x.Route == _route).Name ?? "Your library";
    private string LibraryKey => Api.CachePrefix + ":library:" + _provider;
    private static string Key(MediaItem item) => item.Kind + ":" + item.Id;
    private static string Time(double seconds) => TimeSpan.FromSeconds(Math.Max(0, double.IsFinite(seconds) ? seconds : 0)).ToString(@"hh\:mm\:ss");
    private string CategoryName(string id) => _catalog.Categories.FirstOrDefault(c => c.Id == id)?.Name ?? "";

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _reference = DotNetObjectReference.Create(this);
            await Run(async () =>
            {
                var initial = await JS.InvokeAsync<InitialState>("xtream.init", _reference);
                _online = initial.Online;
                await Api.Initialize();
                var preferences = await Store.GetAsync<Preferences>("preferences");
                if (preferences is not null) { _theme = preferences.Theme; _nameGuide = preferences.NameGuide; }
                await LoadProviders();
                _route = Links.Any(x => x.Route == initial.Route) ? initial.Route : "home";
                _initialized = true;
                await LoadRoute();
            });
            _initialized = true;
            StateHasChanged();
        }
        if (_pendingVideo is { } url)
        {
            _pendingVideo = null;
            try { await JS.InvokeVoidAsync("xtream.play", url, _pendingPosition); }
            catch (JSException e) { _message = e.Message; StateHasChanged(); }
        }
        if (_restoreScroll is { } scroll) { _restoreScroll = null; await JS.InvokeVoidAsync("xtream.restoreScroll", scroll); }
    }
    private async Task Run(Func<Task> action)
    {
        _busy = true; _message = null;
        try { await action(); }
        catch (Exception e) { _message = e is HttpRequestException ? "Cannot reach your provider. Check the connection, HTTPS address and browser access (CORS)." : e.Message; }
        finally { _busy = false; StateHasChanged(); }
    }
    [JSInvokable] public Task Connectivity(bool online) { _online = online; StateHasChanged(); return Task.CompletedTask; }
    [JSInvokable] public Task UpdateAvailable(bool available) { _update = available; StateHasChanged(); return Task.CompletedTask; }
    [JSInvokable] public Task PlayerError(string message) { _message = message; StateHasChanged(); return Task.CompletedTask; }
    [JSInvokable] public async Task RouteChanged(string route)
    {
        await Run(async () =>
        {
            await SaveBrowseState();
            _route = Links.Any(x => x.Route == route) || route == "detail" ? route : "home";
            await LoadRoute();
        });
    }
    private Task Navigate(string route) => JS.InvokeVoidAsync("xtream.navigate", route).AsTask();
    private async Task SaveBrowseState()
    {
        if (_route is "live" or "vod" or "series" or "guide")
            await Store.SetAsync(Api.CachePrefix + ":browse:" + _provider + ":" + _route,
                new BrowseState(_search, _category, _sort, await JS.InvokeAsync<double>("xtream.scroll")));
    }
    private async Task LoadRoute()
    {
        if (_route is "live" or "vod" or "series" or "guide")
        {
            var state = await Store.GetAsync<BrowseState>(Api.CachePrefix + ":browse:" + _provider + ":" + _route);
            _search = state?.Search ?? ""; _category = state?.Category ?? "@all"; _sort = state?.Sort ?? ItemSort.Default;
            await LoadCatalog(false); _restoreScroll = state?.Scroll ?? 0;
        }
        if (_route == "detail" && _selected is null) await Navigate("home");
    }
    private async Task LoadProviders()
    {
        _providers = Api.Providers();
        _provider = await Store.GetAsync<string>(Api.CachePrefix + ":active") ?? _providers.FirstOrDefault()?.Id ?? "";
        if (!_providers.Any(p => p.Id == _provider)) _provider = _providers.FirstOrDefault()?.Id ?? "";
        await LoadLibrary();
    }
    private async Task LoadLibrary() => _library = await Store.GetAsync<LocalLibrary>(LibraryKey) ?? new();
    private ValueTask SaveLibrary() => Store.SetAsync(LibraryKey, _library);
    private async Task ProviderChanged(ChangeEventArgs e)
    {
        await Run(async () =>
        {
            await StopPlayback(); await SaveBrowseState();
            _provider = e.Value?.ToString() ?? "";
            await Store.SetAsync(Api.CachePrefix + ":active", _provider);
            await LoadLibrary(); _selected = null; _detail = null;
            if (_route == "detail") await Navigate("home"); else await LoadRoute();
        });
    }
    private async Task LoadCatalog(bool refresh)
    {
        if (_provider.Length == 0) { _catalog = new([], [], default); _visible = []; return; }
        var key = Api.CachePrefix + ":catalog:" + _provider + ":" + Kind;
        _catalog = await Store.GetAsync<BrowseCatalog>(key) ?? new([], [], default);
        if (_online && (refresh || _catalog.LoadedAt < DateTimeOffset.UtcNow.AddHours(-12)))
        {
            try { _catalog = await Api.Browse(_provider, Kind, refresh); await Store.SetAsync(key, _catalog); }
            catch when (_catalog.Items.Count > 0) { _message = "Showing the saved catalog. Refresh when your provider is available."; }
        }
        var hidden = new HiddenCategoryResolver(_library.HiddenCategories.Where(k => k.StartsWith(Kind + ":"))
            .Select(k => new HiddenCategory { ProfileId = _provider, Scope = Enum.Parse<CategoryScope>(Kind, true),
                CategoryId = k[(Kind.Length + 1)..], CategoryName = _library.HiddenCategoryNames.GetValueOrDefault(k) }));
        foreach (var c in _catalog.Categories) hidden.IsHidden(c.Id, c.Name);
        foreach (var repoint in hidden.Repoints)
        {
            var oldKey = Kind + ":" + repoint.OldCategoryId;
            var newKey = Kind + ":" + repoint.NewCategoryId;
            _library.HiddenCategories.Remove(oldKey); _library.HiddenCategories.Add(newKey);
            _library.HiddenCategoryNames[newKey] = _library.HiddenCategoryNames.GetValueOrDefault(oldKey) ?? "";
            _library.HiddenCategoryNames.Remove(oldKey);
        }
        if (hidden.Repoints.Count > 0) await SaveLibrary();
        _browse = new(_catalog.Items, (x, i) => new(x.Id, x.Name, x.CategoryId, x.CategoryOrder, x.Added, i));
        Filter();
        if (_route == "guide") await LoadGuide(refresh);
    }
    private void Filter()
    {
        CategorySelection selection = _category switch
        {
            "@favorites" => CategorySelection.Favorites.Instance, "@recent" => CategorySelection.Recent.Instance,
            "@all" => CategorySelection.All.Instance,
            _ => new CategorySelection.Real(_category, CategoryName(_category), 0)
        };
        _visible = _browse?.Apply(new FilterRequest { Selection = selection, SearchText = _search, Sort = _sort,
            FavoriteIds = _library.Favorites.Where(x => x.StartsWith(Kind + ":")).Select(x => x[(Kind.Length + 1)..]).ToHashSet(),
            HiddenCategoryIds = _library.HiddenCategories.Where(x => x.StartsWith(Kind + ":")).Select(x => x[(Kind.Length + 1)..]).ToHashSet(),
            RecentIds = _library.RecentChannels }).ToList() ?? [];
    }
    private void SearchChanged(ChangeEventArgs e) { _search = e.Value?.ToString() ?? ""; Filter(); }
    private void CategoryChanged(ChangeEventArgs e) { _category = e.Value?.ToString() ?? "@all"; Filter(); }
    private void SortChanged(ChangeEventArgs e) { Enum.TryParse(e.Value?.ToString(), out _sort); Filter(); }
    private async Task ToggleHidden()
    {
        var key = Kind + ":" + _category;
        if (!_library.HiddenCategories.Add(key)) { _library.HiddenCategories.Remove(key); _library.HiddenCategoryNames.Remove(key); }
        else _library.HiddenCategoryNames[key] = CategoryName(_category);
        await SaveLibrary(); Filter();
    }
    private async Task ToggleFavorite(MediaItem item)
    {
        if (!_library.Favorites.Add(Key(item))) _library.Favorites.Remove(Key(item));
        await SaveLibrary(); Filter();
    }
    private async Task Select(MediaItem item)
    {
        if (item.Kind == "live") { await Play(item); return; }
        _selected = item; _returnRoute = _route;
        var key = Api.CachePrefix + ":detail:" + _provider + ":" + Key(item);
        _detail = await Store.GetAsync<MediaDetail>(key);
        if (_online) { _detail = await Api.Detail(_provider, item.Kind, item.Id); await Store.SetAsync(key, _detail); }
        await Navigate("detail");
    }
    private PlayRequest Request(MediaItem item) => new(_provider, item.Kind, item.Id, item.SeriesId,
        PositionSeconds: _library.Progress.GetValueOrDefault(Key(item)) is { } p && p.Position / Math.Max(p.Duration, 1) < .95 ? p.Position : 0);
    private async Task Play(MediaItem item)
    {
        if (!_online) throw new InvalidOperationException("Connect to the internet to stream from your provider.");
        await StopPlayback();
        _request = Request(item); _playing = item;
        _playLibrary = _library; _playLibraryKey = LibraryKey;
        try { _session = await Api.OpenAsync(_request); }
        catch { _playing = null; throw; }
        _position = _request.PositionSeconds;
        _pendingVideo = _session.Url; _pendingPosition = _request.PositionSeconds; _lastCheckpoint = null;
        if (item.Kind == "live")
        {
            _library.RecentChannels.Remove(item.Id); _library.RecentChannels.Insert(0, item.Id);
            if (_library.RecentChannels.Count > 50) _library.RecentChannels.RemoveAt(50);
            await SaveLibrary();
        }
        _restoreScroll = 0;
    }
    private async Task StopPlayback()
    {
        await JS.InvokeVoidAsync("xtream.stop");
        if (_session is not null) { try { await Api.CloseAsync(_session.Id); } catch { } }
        _session = null; _playing = null; _request = null; _lastCheckpoint = null;
    }
    [JSInvokable] public async Task Progress(double position, double duration)
    {
        if (_playing is null || _playing.Kind == "local" || _playLibraryKey.Length == 0 ||
            (_request?.Kind == "live" && _request.Start is null)) return;
        _position = position + (_session?.OffsetSeconds ?? 0);
        var total = _session?.DurationSeconds > 0 ? _session.DurationSeconds : duration;
        if (!double.IsFinite(_position) || !double.IsFinite(total) || total <= 0) return;
        var at = TimeSpan.FromSeconds(_position);
        if (ResumePolicy.ShouldCheckpoint(at, TimeSpan.FromSeconds(total), _lastCheckpoint))
        {
            _playLibrary.Progress[Key(_playing)] = new(_playing, _position, total, DateTimeOffset.UtcNow);
            _lastCheckpoint = at;
            await Store.SetAsync(_playLibraryKey, _playLibrary);
        }
    }
    private async Task Catchup(MediaItem item, EpgProgramme entry)
    {
        await StopPlayback(); _playing = item;
        _playLibrary = _library; _playLibraryKey = LibraryKey;
        _request = new(_provider, "live", item.Id, Start: entry.Start, DurationMinutes: (int)Math.Ceiling(entry.Duration.TotalMinutes));
        try { _session = await Api.OpenAsync(_request); } catch { _playing = null; throw; }
        _pendingVideo = _session.Url; _pendingPosition = 0; _restoreScroll = 0;
    }
    private async Task LoadGuide(bool refresh)
    {
        var key = Api.CachePrefix + ":guide:" + _provider + ":" + _guideFrom.ToString("yyyyMMddHH");
        if (_online)
        {
            try { _guide = await Api.Guide(_provider, _guideFrom, _guideFrom.AddHours(6), _nameGuide); }
            catch
            {
                _guide = await Store.GetAsync<Dictionary<string, List<EpgProgramme>>>(key) ?? [];
                _message = "The full guide is unavailable. Use Load now / next for a channel’s provider guide.";
            }
            await Store.SetAsync(key, _guide);
        }
        else _guide = await Store.GetAsync<Dictionary<string, List<EpgProgramme>>>(key) ?? [];
    }
    private async Task ShiftGuide(int hours) { _guideFrom = _guideFrom.AddHours(hours); await LoadGuide(false); }
    private async Task ShortGuide(MediaItem item)
    {
        var entries = await Api.ShortGuide(_provider, item.Id);
        _guide[item.Id] = entries.Select(e => new EpgProgramme { XmltvChannelId = item.Id, Start = e.Start, Stop = e.Stop, Title = e.Title, Description = e.Description }).ToList();
    }
    private async Task AddProvider()
    {
        var p = await Api.Add(new SaveProvider(_providerName, _providerUrl, _username, _password));
        _password = ""; _username = ""; _providerName = ""; _providerUrl = "";
        await Store.SetAsync(Api.CachePrefix + ":active", p.Id); await LoadProviders();
        _message = "Provider connected directly. Open Live TV, Movies or Series to play.";
    }
    private async Task DeleteProvider(string id)
    {
        if (_provider == id) await StopPlayback();
        await Api.Remove(id); await LoadProviders();
    }
    private async Task SavePreferences() => await Store.SetAsync("preferences", new Preferences(_theme, _nameGuide));
    private async Task PlayLocal()
    {
        await StopPlayback();
        var file = await JS.InvokeAsync<LocalFile?>("xtream.localFile");
        if (file is null) return;
        _playing = new("local", file.Title, "", 0, "local");
        _pendingVideo = file.Url; _pendingPosition = 0; _restoreScroll = 0;
    }
    private Task UpdateApp() => JS.InvokeVoidAsync("xtream.update").AsTask();
    public ValueTask DisposeAsync() { _reference?.Dispose(); return ValueTask.CompletedTask; }
    private sealed record InitialState(string Route, bool Online);
    private sealed record BrowseState(string Search, string Category, ItemSort Sort, double Scroll);
    private sealed record Preferences(string Theme, bool NameGuide);
    private sealed record LocalFile(string Url, string Title);
}
