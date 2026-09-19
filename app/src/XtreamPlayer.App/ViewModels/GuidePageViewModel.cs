using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using XtreamPlayer.App.Controls;
using XtreamPlayer.Core.Abstractions;
using XtreamPlayer.Core.Library;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Core.Navigation;
using XtreamPlayer.Infrastructure.Epg;
using XtreamPlayer.Infrastructure.Xtream;

namespace XtreamPlayer.App.ViewModels;

public sealed record ZoomLevel(string Label, double PixelsPerMinute);

/// <summary>
/// The XMLTV guide grid.
/// </summary>
/// <remarks>
/// New in the rewrite. The previous app parsed <c>xmltv.php</c> only to count
/// its elements, and every channel's current programme was the literal string
/// "Use short EPG sync for now/next".
/// </remarks>
public sealed partial class GuidePageViewModel(
    IEpgService epg,
    ICatalogStore catalog,
    IProfileRepository profiles,
    IFavoritesStore favorites,
    ISettingsRepository settings,
    INavigationService navigation,
    ILogger<GuidePageViewModel> log) : PageViewModelBase
{
    /// <summary>
    /// How far back the grid starts. Enough to see what has just finished
    /// without making "now" feel buried.
    /// </summary>
    private static readonly TimeSpan LookBehind = TimeSpan.FromHours(2);

    private static readonly TimeSpan LookAhead = TimeSpan.FromHours(28);

    private Profile? _profile;

    [ObservableProperty]
    private IReadOnlyList<EpgRow> _rows = [];

    [ObservableProperty]
    private DateTimeOffset _windowStart = DateTimeOffset.Now - LookBehind;

    [ObservableProperty]
    private double _pixelsPerMinute = 6;

    [ObservableProperty]
    private EpgProgramme? _selectedProgramme;

    [ObservableProperty]
    private string? _selectedChannelName;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    private bool _favoritesOnly;

    [ObservableProperty]
    private int _matchedChannels;

    [ObservableProperty]
    private int _totalChannels;

    public override PageKey Key => PageKey.Guide;

    public override string Title => "Guide";

    public TimeSpan WindowLength => LookBehind + LookAhead;

    public IReadOnlyList<ZoomLevel> ZoomLevels { get; } =
    [
        new("30 min", 12),
        new("1 hour", 6),
        new("2 hours", 3),
        new("4 hours", 1.5),
    ];

    [ObservableProperty]
    private ZoomLevel? _zoom;

    public ObservableCollection<string> ChannelNames { get; } = [];

    public bool HasRows => Rows.Count > 0;

    public string MatchSummary => TotalChannels == 0
        ? string.Empty
        : $"{MatchedChannels:N0} of {TotalChannels:N0} channels matched to the guide";

    public override async Task OnNavigatedToAsync(
        object? parameter, bool isNewInstance, CancellationToken ct = default)
    {
        if (!isNewInstance)
        {
            // Time has moved on; the grid would otherwise still be anchored to
            // whenever the page was first opened.
            WindowStart = DateTimeOffset.Now - LookBehind;
            await LoadAsync(ct).ConfigureAwait(false);
            return;
        }

        Zoom = ZoomLevels[1];

        var activeId = await settings.GetAsync(SettingKeys.ActiveProfileId, ct).ConfigureAwait(false);
        var all = await profiles.ListAsync(ct).ConfigureAwait(false);
        _profile = all.FirstOrDefault(p => p.Id == activeId) ?? all.FirstOrDefault();

        if (_profile is null)
        {
            Status = "No provider configured.";
            return;
        }

        epg.Progress += OnProgress;

        await LoadAsync(ct).ConfigureAwait(false);

        // Refreshing only when stale keeps opening the guide instant most of the
        // time; a full feed is far too slow to fetch on every visit.
        var ttlHours = int.TryParse(
            await settings.GetAsync(SettingKeys.EpgTtlHours, ct).ConfigureAwait(false),
            out var hours) && hours > 0 ? hours : 12;

        _ = RefreshIfStaleAsync(TimeSpan.FromHours(ttlHours));
    }

    partial void OnZoomChanged(ZoomLevel? value)
    {
        if (value is not null)
        {
            PixelsPerMinute = value.PixelsPerMinute;
        }
    }

    partial void OnFavoritesOnlyChanged(bool value) => _ = LoadAsync(CancellationToken.None);

    private async Task RefreshIfStaleAsync(TimeSpan ttl)
    {
        if (_profile is null)
        {
            return;
        }

        IsRefreshing = true;
        try
        {
            await epg.RefreshIfStaleAsync(_profile, catalog.Current.Live.Items, ttl)
                .ConfigureAwait(true);

            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Guide refresh failed.");
            Status = $"Guide refresh failed: {ex.Message}";
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_profile is null || IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;
        try
        {
            await epg.RefreshAsync(_profile, catalog.Current.Live.Items).ConfigureAwait(true);
            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        if (_profile is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var scope = new ProfileScope(_profile.Id);

            var guide = await epg
                .GetGuideAsync(scope, WindowStart, WindowStart + WindowLength, ct)
                .ConfigureAwait(true);

            var mappings = await epg.GetMappingsAsync(scope, ct).ConfigureAwait(true);

            var channels = catalog.Current.Live.Items;
            TotalChannels = channels.Count;
            MatchedChannels = mappings.Values.Count(m => m.IsMatched);

            var favoriteIds = favorites.IdsFor(ContentKind.Live);

            var rows = new List<EpgRow>(channels.Count);
            ChannelNames.Clear();

            foreach (var channel in channels)
            {
                if (FavoritesOnly && !favoriteIds.Contains(channel.Id))
                {
                    continue;
                }

                // Channels with no guide data still get a row. Hiding them would
                // make the grid disagree with the channel list, and an empty row
                // at least says the channel exists.
                guide.TryGetValue(channel.Id, out var programmes);

                rows.Add(new EpgRow(channel.Id, channel.Name, programmes ?? []));
                ChannelNames.Add(channel.Name);
            }

            Rows = rows;

            OnPropertyChanged(nameof(HasRows));
            OnPropertyChanged(nameof(MatchSummary));

            var state = await epg.GetStateAsync(scope, ct).ConfigureAwait(true);

            Status = state.Error is { } error
                ? error
                : state.LastRefresh is { } at
                    ? $"Guide as of {at.ToLocalTime():HH:mm}, {state.ProgrammeCount:N0} programmes"
                    : "No guide data yet.";
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not load the guide.");
            Status = $"Could not load the guide: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnProgress(object? sender, EpgRefreshProgress progress) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Status = progress.Message);

    public void SelectProgramme(EpgRow row, EpgProgramme programme)
    {
        SelectedProgramme = programme;
        SelectedChannelName = row.ChannelName;
    }

    /// <summary>
    /// Double-clicking a programme plays its channel when it is on now.
    /// </summary>
    /// <remarks>
    /// A future programme cannot be played, so activating one does nothing yet;
    /// recording is where that hooks up.
    /// </remarks>
    public async Task ActivateAsync(EpgRow row, EpgProgramme programme)
    {
        if (!programme.IsNowPlaying(DateTimeOffset.Now))
        {
            return;
        }

        var channel = catalog.Current.Live.Items.FirstOrDefault(c => c.Id == row.ChannelId);

        if (channel is null)
        {
            return;
        }

        await navigation.NavigateAsync(new NavRoute(PageKey.Player, new PlaybackRequestParameter(
            channel.StreamUrl, channel.Name, ContentKind.Live, channel.Id,
            Subtitle: programme.Title))).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task PlaySelectedAsync()
    {
        if (SelectedProgramme is null)
        {
            return;
        }

        var row = Rows.FirstOrDefault(r => r.ChannelName == SelectedChannelName);

        if (row is not null)
        {
            await ActivateAsync(row, SelectedProgramme).ConfigureAwait(false);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            epg.Progress -= OnProgress;
        }

        base.Dispose(disposing);
    }
}
