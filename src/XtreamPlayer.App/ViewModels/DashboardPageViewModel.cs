using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XtreamPlayer.Core.Abstractions;
using XtreamPlayer.Core.Library;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Core.Navigation;
using XtreamPlayer.Infrastructure.Xtream;

namespace XtreamPlayer.App.ViewModels;

/// <summary>A single row in the dashboard's cross-catalog search results.</summary>
public sealed record SearchHit(ContentKind Kind, string Id, string Title, string? PosterUrl, string Context)
{
    public string KindLabel => Kind switch
    {
        ContentKind.Live => "Live",
        ContentKind.Vod => "Movie",
        _ => "Series",
    };
}

public sealed partial class ContinueWatchingCard(ResumeItem item, string? posterUrl) : ObservableObject
{
    public ResumeItem Item { get; } = item;
    public string Title => Item.Title ?? "Untitled";
    public string? Detail => Item.Detail;
    public string? PosterUrl { get; } = posterUrl;
    public double Progress => Item.ProgressPercent / 100.0;

    public string RemainingLabel => Item.Duration > TimeSpan.Zero
        ? $"{VodDetailPageViewModel.Format(Item.Duration - Item.Position)} left"
        : $"{Item.ProgressPercent}%";
}

public sealed partial class DashboardPageViewModel(
    ICatalogStore catalog,
    IFavoritesStore favorites,
    IResumeRepository resume,
    IProfileRepository profiles,
    ISettingsRepository settings,
    INavigationService navigation) : PageViewModelBase
{
    private const int MaxHitsPerKind = 12;

    private CancellationTokenSource? _searching;
    private ProfileScope? _scope;
    private bool _subscribed;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    public override PageKey Key => PageKey.Dashboard;

    public override string Title => "Home";

    public ObservableCollection<ContinueWatchingCard> ContinueWatching { get; } = [];

    public ObservableCollection<SearchHit> Results { get; } = [];

    public ObservableCollection<SearchHit> Favorites { get; } = [];

    public bool HasResults => Results.Count > 0;

    public bool HasContinueWatching => ContinueWatching.Count > 0;

    public bool HasFavorites => Favorites.Count > 0;

    public override async Task OnNavigatedToAsync(
        object? parameter, bool isNewInstance, CancellationToken ct = default)
    {
        // A search term can arrive from the shell's search box at any time, even
        // when returning to a cached dashboard.
        if (parameter is string term && !string.IsNullOrWhiteSpace(term))
        {
            SearchText = term;
        }

        if (!isNewInstance)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var activeId = await settings.GetAsync(SettingKeys.ActiveProfileId, ct).ConfigureAwait(false);
            var all = await profiles.ListAsync(ct).ConfigureAwait(false);
            var profile = all.FirstOrDefault(p => p.Id == activeId) ?? all.FirstOrDefault();

            if (profile is null)
            {
                return;
            }

            _scope = new ProfileScope(profile.Id);

            if (!_subscribed)
            {
                // The dashboard is usually the first page on screen, so it
                // loads BEFORE the catalog sync finishes. Without this every
                // favorite renders as Unavailable and never corrects itself.
                catalog.Changed += OnCatalogChanged;
                _subscribed = true;
            }

            await RefreshContinueWatchingAsync(ct).ConfigureAwait(false);
            RefreshFavorites();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public override Task OnRestoredAsync()
    {
        // Progress moves while the player is open, so the row is stale by the
        // time the user comes back to it.
        _ = RefreshContinueWatchingAsync(CancellationToken.None);
        RefreshFavorites();

        return base.OnRestoredAsync();
    }

    private void OnCatalogChanged(object? sender, CatalogState state) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            RefreshFavorites();
            _ = RefreshContinueWatchingAsync(CancellationToken.None);
        });

    private async Task RefreshContinueWatchingAsync(CancellationToken ct)
    {
        if (_scope is not { } scope)
        {
            return;
        }

        var items = await resume.ListContinueWatchingAsync(scope, 20, ct).ConfigureAwait(false);

        ContinueWatching.Clear();

        foreach (var item in items)
        {
            ContinueWatching.Add(new ContinueWatchingCard(item, PosterFor(item.Kind, item.ContentId)));
        }

        OnPropertyChanged(nameof(HasContinueWatching));
    }

    private void RefreshFavorites()
    {
        Favorites.Clear();

        foreach (var favorite in favorites.All.Take(24))
        {
            // Rendered from the favorites table itself, left-joined against the
            // catalog. The old app filtered the catalog by favorite id, so a
            // favorite whose item was not loaded silently vanished; here it
            // shows with no artwork and can still be removed.
            Favorites.Add(new SearchHit(
                favorite.Kind,
                favorite.ContentId,
                favorite.Title ?? favorite.ContentId,
                PosterFor(favorite.Kind, favorite.ContentId),
                ResolveContext(favorite.Kind, favorite.ContentId)));
        }

        OnPropertyChanged(nameof(HasFavorites));
    }

    private string ResolveContext(ContentKind kind, string contentId) => kind switch
    {
        ContentKind.Live =>
            catalog.Current.Live.Items.Any(i => i.Id == contentId) ? "Live" : "Unavailable",
        ContentKind.Vod =>
            catalog.Current.Vod.Items.Any(i => i.Id == contentId) ? "Movie" : "Unavailable",
        _ => catalog.Current.Series.Items.Any(i => i.Id == contentId) ? "Series" : "Unavailable",
    };

    private string? PosterFor(ContentKind kind, string contentId) => kind switch
    {
        ContentKind.Vod => catalog.Current.Vod.Items.FirstOrDefault(i => i.Id == contentId)?.PosterUrl,
        ContentKind.Series => catalog.Current.Series.Items.FirstOrDefault(i => i.Id == contentId)?.PosterUrl,
        ContentKind.Live => catalog.Current.Live.Items.FirstOrDefault(i => i.Id == contentId)?.IconUrl,
        _ => null,
    };

    partial void OnSearchTextChanged(string value) => RunSearch(value);

    /// <summary>
    /// Cross-catalog search, debounced and run off the UI thread.
    /// </summary>
    /// <remarks>
    /// Three catalogs totalling ~88,000 items are scanned per keystroke, so this
    /// cannot run inline without dropping frames while typing.
    /// </remarks>
    private void RunSearch(string term)
    {
        _searching?.Cancel();
        _searching?.Dispose();

        var trimmed = term.Trim();

        if (trimmed.Length < 2)
        {
            Results.Clear();
            OnPropertyChanged(nameof(HasResults));
            IsSearching = false;
            return;
        }

        var cts = new CancellationTokenSource();
        _searching = cts;
        IsSearching = true;

        var state = catalog.Current;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200, cts.Token).ConfigureAwait(false);

                var hits = new List<SearchHit>();

                foreach (var channel in state.Live.Items)
                {
                    if (hits.Count >= MaxHitsPerKind)
                    {
                        break;
                    }

                    if (channel.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                    {
                        hits.Add(new SearchHit(ContentKind.Live, channel.Id, channel.Name,
                            channel.IconUrl, channel.CategoryName ?? "Live"));
                    }
                }

                var vodCount = 0;
                foreach (var movie in state.Vod.Items)
                {
                    if (vodCount >= MaxHitsPerKind)
                    {
                        break;
                    }

                    if (movie.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                    {
                        hits.Add(new SearchHit(ContentKind.Vod, movie.Id, movie.Name,
                            movie.PosterUrl, movie.CategoryName ?? "Movie"));
                        vodCount++;
                    }
                }

                var seriesCount = 0;
                foreach (var series in state.Series.Items)
                {
                    if (seriesCount >= MaxHitsPerKind)
                    {
                        break;
                    }

                    if (series.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                    {
                        hits.Add(new SearchHit(ContentKind.Series, series.Id, series.Name,
                            series.PosterUrl, series.CategoryName ?? "Series"));
                        seriesCount++;
                    }
                }

                if (cts.IsCancellationRequested)
                {
                    return;
                }

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (cts.IsCancellationRequested)
                    {
                        return;
                    }

                    Results.Clear();
                    foreach (var hit in hits)
                    {
                        Results.Add(hit);
                    }

                    OnPropertyChanged(nameof(HasResults));
                    IsSearching = false;
                });
            }
            catch (OperationCanceledException)
            {
                // Superseded by the next keystroke.
            }
        }, cts.Token);
    }

    [RelayCommand]
    private async Task OpenAsync(SearchHit? hit)
    {
        if (hit is null)
        {
            return;
        }

        switch (hit.Kind)
        {
            case ContentKind.Vod:
                await navigation.NavigateAsync(new NavRoute(PageKey.VodDetail, hit.Id))
                    .ConfigureAwait(false);
                break;

            case ContentKind.Series:
                await navigation.NavigateAsync(new NavRoute(PageKey.SeriesDetail, hit.Id))
                    .ConfigureAwait(false);
                break;

            case ContentKind.Live:
                var channel = catalog.Current.Live.Items.FirstOrDefault(c => c.Id == hit.Id);
                if (channel is not null)
                {
                    await navigation.NavigateAsync(new NavRoute(PageKey.Player,
                        new PlaybackRequestParameter(channel.StreamUrl, channel.Name,
                            ContentKind.Live, channel.Id))).ConfigureAwait(false);
                }

                break;
        }
    }

    [RelayCommand]
    private async Task ResumeAsync(ContinueWatchingCard? card)
    {
        if (card is null)
        {
            return;
        }

        var item = card.Item;

        var url = item.Kind switch
        {
            ContentKind.Vod => catalog.Current.Vod.Items
                .FirstOrDefault(v => v.Id == item.ContentId)?.StreamUrl,
            _ => null,
        };

        if (url is null)
        {
            // Episodes are not in the flat catalog; the series page owns them.
            if (item.SeriesId is not null)
            {
                await navigation.NavigateAsync(new NavRoute(PageKey.SeriesDetail, item.SeriesId))
                    .ConfigureAwait(false);
            }

            return;
        }

        await navigation.NavigateAsync(new NavRoute(PageKey.Player, new PlaybackRequestParameter(
            url, item.Title ?? "Resume", item.Kind, item.ContentId, item.Position, item.Detail)))
            .ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task RemoveResumeAsync(ContinueWatchingCard? card)
    {
        if (card is null || _scope is not { } scope)
        {
            return;
        }

        await resume.RemoveAsync(scope, card.Item.ContentId, card.Item.Kind).ConfigureAwait(false);
        ContinueWatching.Remove(card);
        OnPropertyChanged(nameof(HasContinueWatching));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_subscribed)
            {
                catalog.Changed -= OnCatalogChanged;
            }

            _searching?.Cancel();
            _searching?.Dispose();
        }

        base.Dispose(disposing);
    }
}
