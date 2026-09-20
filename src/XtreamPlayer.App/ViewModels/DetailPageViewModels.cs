using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XtreamPlayer.Core.Abstractions;
using XtreamPlayer.Core.Library;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Core.Navigation;
using XtreamPlayer.Infrastructure.Downloads;
using XtreamPlayer.Infrastructure.Xtream;

namespace XtreamPlayer.App.ViewModels;

public sealed partial class VodDetailPageViewModel(
    ICatalogStore catalog,
    IFavoritesStore favorites,
    IResumeRepository resume,
    IProfileRepository profiles,
    ISettingsRepository settings,
    INavigationService navigation,
    IDownloadManager downloads,
    IDownloadPaths downloadPaths) : PageViewModelBase
{
    [ObservableProperty]
    private VodItem? _item;

    [ObservableProperty]
    private VodDetail? _detail;

    [ObservableProperty]
    private bool _isFavorite;

    [ObservableProperty]
    private ResumeItem? _resumePoint;

    [ObservableProperty]
    private string? _error;

    public override PageKey Key => PageKey.VodDetail;

    public override string Title => Item?.Name ?? "Movie";

    public bool CanResume => ResumePoint is { Finished: false, ProgressPercent: > 0 and < 95 };

    public string ResumeLabel => ResumePoint is null
        ? "Resume"
        : $"Resume from {Format(ResumePoint.Position)}";

    public override async Task OnNavigatedToAsync(
        object? parameter, bool isNewInstance, CancellationToken ct = default)
    {
        if (!isNewInstance || parameter is not string vodId)
        {
            return;
        }

        IsBusy = true;
        try
        {
            Item = catalog.Current.Vod.Items.FirstOrDefault(i => i.Id == vodId);
            IsFavorite = favorites.IsFavorite(ContentKind.Vod, vodId);

            // Detail is a separate request, and the page is usable without it —
            // the poster, title and Play button all come from the catalog item.
            Detail = await catalog.GetVodDetailAsync(vodId, ct).ConfigureAwait(false);

            var profile = await ActiveProfileAsync(ct).ConfigureAwait(false);
            if (profile is not null)
            {
                ResumePoint = await resume
                    .GetAsync(new ProfileScope(profile.Id), vodId, ContentKind.Vod, ct)
                    .ConfigureAwait(false);
            }

            OnPropertyChanged(nameof(CanResume));
            OnPropertyChanged(nameof(ResumeLabel));
            OnPropertyChanged(nameof(Title));
        }
        catch (XtreamException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<Profile?> ActiveProfileAsync(CancellationToken ct)
    {
        var activeId = await settings.GetAsync(SettingKeys.ActiveProfileId, ct).ConfigureAwait(false);
        var all = await profiles.ListAsync(ct).ConfigureAwait(false);
        return all.FirstOrDefault(p => p.Id == activeId) ?? all.FirstOrDefault();
    }

    /// <summary>Plays from the beginning.</summary>
    /// <remarks>
    /// Play and Resume are separate commands rather than one taking a bool.
    /// A RelayCommand&lt;bool&gt; bound to CommandParameter="True" receives the
    /// string "True" and throws when the button attaches to the logical tree,
    /// inside a layout pass, which is fatal.
    /// </remarks>
    [RelayCommand]
    private Task PlayAsync() => StartAsync(fromStart: true);

    /// <summary>Picks up from the last saved position.</summary>
    [RelayCommand]
    private Task ResumeAsync() => StartAsync(fromStart: false);

    private async Task StartAsync(bool fromStart)
    {
        if (Item is null)
        {
            return;
        }

        var start = !fromStart && CanResume ? ResumePoint!.Position : (TimeSpan?)null;

        await navigation.NavigateAsync(new NavRoute(PageKey.Player, new PlaybackRequestParameter(
            Item.StreamUrl, Item.Name, ContentKind.Vod, Item.Id, start,
            Detail?.Genre))).ConfigureAwait(false);
    }

    /// <summary>Queues the movie for offline viewing.</summary>
    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (Item is null)
        {
            return;
        }

        var profile = await ActiveProfileAsync(CancellationToken.None).ConfigureAwait(true);

        if (profile is null)
        {
            return;
        }

        try
        {
            var target = await downloadPaths.ForMovieAsync(Item).ConfigureAwait(true);

            await downloads.EnqueueAsync(
                new ProfileScope(profile.Id), ContentKind.Vod, Item.Id, Item.Name,
                Item.StreamUrl, target).ConfigureAwait(true);

            DownloadStatus = "Added to downloads.";
        }
        catch (Exception ex)
        {
            DownloadStatus = $"Could not start the download: {ex.Message}";
        }
    }

    [ObservableProperty]
    private string? _downloadStatus;

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        if (Item is null)
        {
            return;
        }

        IsFavorite = await favorites
            .ToggleAsync(ContentKind.Vod, Item.Id, Item.Name)
            .ConfigureAwait(false);
    }

    internal static string Format(TimeSpan value) => value.TotalHours >= 1
        ? value.ToString(@"h\:mm\:ss")
        : value.ToString(@"m\:ss");
}

public sealed partial class EpisodeViewModel(SeriesEpisode episode, ResumeItem? resume)
    : ObservableObject
{
    public SeriesEpisode Episode { get; } = episode;

    public string Title => Episode.Title;
    public int Number => Episode.EpisodeNumber;
    public string? Plot => Episode.Plot;
    public string? StillUrl => Episode.StillUrl;
    public string? DurationLabel => Episode.DurationLabel;

    public string NumberLabel => Episode.EpisodeNumber.ToString();

    /// <summary>Drives the progress bar under a partly watched episode.</summary>
    public double Progress { get; } = resume is null ? 0 : resume.ProgressPercent / 100.0;

    public bool IsWatched { get; } = resume?.Finished ?? false;

    public bool HasProgress => Progress is > 0.01 and < 0.95;
}

public sealed partial class SeriesDetailPageViewModel(
    ICatalogStore catalog,
    IFavoritesStore favorites,
    IResumeRepository resume,
    IProfileRepository profiles,
    ISettingsRepository settings,
    INavigationService navigation,
    IDownloadManager downloads,
    IDownloadPaths downloadPaths) : PageViewModelBase
{
    private SeriesDetail? _detail;
    private ProfileScope? _scope;

    [ObservableProperty]
    private SeriesItem? _item;

    [ObservableProperty]
    private SeriesSeason? _selectedSeason;

    [ObservableProperty]
    private bool _isFavorite;

    [ObservableProperty]
    private string? _error;

    public override PageKey Key => PageKey.SeriesDetail;

    public override string Title => Item?.Name ?? _detail?.Name ?? "Series";

    /// <summary>
    /// Year, genre and rating on one line under the title.
    /// </summary>
    /// <remarks>
    /// The year comes from the detail response when it has loaded and the grid
    /// item otherwise, because the list endpoint carries it too — so it shows
    /// immediately rather than appearing a second later.
    /// </remarks>
    public string MetaLine
    {
        get
        {
            var parts = new List<string>(3);

            if ((_detail?.Year ?? Item?.Year) is { } year)
            {
                parts.Add(year.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            var genre = _detail?.Genre ?? Item?.Genre;

            if (!string.IsNullOrWhiteSpace(genre))
            {
                parts.Add(genre.Trim());
            }

            var rating = _detail?.Rating ?? Item?.Rating;

            if (!string.IsNullOrWhiteSpace(rating))
            {
                parts.Add($"★ {rating}");
            }

            return string.Join("  ·  ", parts);
        }
    }

    public ObservableCollection<SeriesSeason> Seasons { get; } = [];

    public ObservableCollection<EpisodeViewModel> Episodes { get; } = [];

    public string? Plot => _detail?.Plot ?? Item?.Plot;

    public string? PosterUrl => _detail?.PosterUrl ?? Item?.PosterUrl;

    public override async Task OnNavigatedToAsync(
        object? parameter, bool isNewInstance, CancellationToken ct = default)
    {
        if (!isNewInstance || parameter is not string seriesId)
        {
            return;
        }

        IsBusy = true;
        try
        {
            Item = catalog.Current.Series.Items.FirstOrDefault(i => i.Id == seriesId);
            IsFavorite = favorites.IsFavorite(ContentKind.Series, seriesId);

            var profile = await ActiveProfileAsync(ct).ConfigureAwait(false);
            _scope = profile is null ? null : new ProfileScope(profile.Id);

            _detail = await catalog.GetSeriesDetailAsync(seriesId, ct).ConfigureAwait(false);

            if (_detail is null)
            {
                Error = "This series has no episode information.";
                return;
            }

            Seasons.Clear();
            foreach (var season in _detail.Seasons)
            {
                Seasons.Add(season);
            }

            // Prefer the lowest real season over Specials, which is season 0 and
            // is almost never what someone wants to start with.
            SelectedSeason = Seasons.FirstOrDefault(s => s.Number > 0) ?? Seasons.FirstOrDefault();

            OnPropertyChanged(nameof(Plot));
            OnPropertyChanged(nameof(PosterUrl));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(MetaLine));
        }
        catch (XtreamException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectedSeasonChanged(SeriesSeason? value) => _ = LoadEpisodesAsync(value);

    private async Task LoadEpisodesAsync(SeriesSeason? season)
    {
        Episodes.Clear();

        if (season is null || _detail is null
            || !_detail.EpisodesBySeason.TryGetValue(season.Number, out var episodes))
        {
            return;
        }

        foreach (var episode in episodes)
        {
            ResumeItem? point = null;

            if (_scope is { } scope)
            {
                point = await resume.GetAsync(scope, episode.Id, ContentKind.Series)
                    .ConfigureAwait(true);
            }

            Episodes.Add(new EpisodeViewModel(episode, point));
        }
    }

    private async Task<Profile?> ActiveProfileAsync(CancellationToken ct)
    {
        var activeId = await settings.GetAsync(SettingKeys.ActiveProfileId, ct).ConfigureAwait(false);
        var all = await profiles.ListAsync(ct).ConfigureAwait(false);
        return all.FirstOrDefault(p => p.Id == activeId) ?? all.FirstOrDefault();
    }

    [RelayCommand]
    private async Task PlayEpisodeAsync(EpisodeViewModel? episode)
    {
        if (episode is null || _detail is null)
        {
            return;
        }

        // The whole season becomes the playlist, so next/previous episode works
        // from inside the player without coming back here.
        var playlist = Episodes
            .Select(e => new PlaylistEntryParameter(
                e.Episode.Id, e.Title, e.Episode.StreamUrl,
                e.Episode.SeasonNumber, e.Episode.EpisodeNumber))
            .ToList();

        var index = Episodes.IndexOf(episode);
        var start = episode.HasProgress && _scope is { } scope
            ? (await resume.GetAsync(scope, episode.Episode.Id, ContentKind.Series)
                .ConfigureAwait(false))?.Position
            : null;

        await navigation.NavigateAsync(new NavRoute(PageKey.Player, new PlaybackRequestParameter(
            episode.Episode.StreamUrl,
            episode.Title,
            ContentKind.Series,
            episode.Episode.Id,
            start,
            $"{Item?.Name ?? _detail.Name} - S{episode.Episode.SeasonNumber:00}E{episode.Number:00}",
            playlist,
            index))).ConfigureAwait(false);
    }

    [ObservableProperty]
    private string? _downloadStatus;

    /// <summary>Queues one episode.</summary>
    [RelayCommand]
    private async Task DownloadEpisodeAsync(EpisodeViewModel? episode)
    {
        if (episode is null)
        {
            return;
        }

        var queued = await QueueEpisodesAsync([episode]).ConfigureAwait(true);

        DownloadStatus = queued == 0
            ? "Could not start the download."
            : $"Added {episode.Title} to downloads.";
    }

    /// <summary>
    /// Queues every episode of the season on screen.
    /// </summary>
    /// <remarks>
    /// They all go into the same queue rather than starting at once: the
    /// manager runs at most two concurrently because each one occupies a
    /// provider connection slot that live playback also needs.
    /// </remarks>
    [RelayCommand]
    private async Task DownloadSeasonAsync()
    {
        if (Episodes.Count == 0)
        {
            return;
        }

        var queued = await QueueEpisodesAsync([.. Episodes]).ConfigureAwait(true);

        DownloadStatus = queued == 0
            ? "Could not start the downloads."
            : $"Added {queued} episode{(queued == 1 ? string.Empty : "s")} to downloads.";
    }

    private async Task<int> QueueEpisodesAsync(IReadOnlyList<EpisodeViewModel> episodes)
    {
        var profile = await ActiveProfileAsync(CancellationToken.None).ConfigureAwait(true);

        if (profile is null || _detail is null)
        {
            return 0;
        }

        var scope = new ProfileScope(profile.Id);
        var seriesName = Item?.Name ?? _detail.Name;
        var queued = 0;

        foreach (var episode in episodes)
        {
            try
            {
                var target = await downloadPaths.ForEpisodeAsync(
                    seriesName,
                    episode.Episode.SeasonNumber,
                    episode.Episode.EpisodeNumber,
                    episode.Title,
                    episode.Episode.ContainerExtension).ConfigureAwait(true);

                await downloads.EnqueueAsync(
                    scope, ContentKind.Series, episode.Episode.Id, episode.Title,
                    episode.Episode.StreamUrl, target).ConfigureAwait(true);

                queued++;
            }
            catch (Exception ex)
            {
                // One bad episode must not abandon the rest of the season.
                DownloadStatus = $"{episode.Title}: {ex.Message}";
            }
        }

        return queued;
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        if (Item is null)
        {
            return;
        }

        IsFavorite = await favorites
            .ToggleAsync(ContentKind.Series, Item.Id, Item.Name)
            .ConfigureAwait(false);
    }
}
