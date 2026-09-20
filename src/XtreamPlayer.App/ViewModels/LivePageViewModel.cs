using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using XtreamPlayer.App.Behaviors;
using XtreamPlayer.Core.Abstractions;
using XtreamPlayer.Core.Library;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Core.Navigation;
using XtreamPlayer.Infrastructure.Xtream;

namespace XtreamPlayer.App.ViewModels;

/// <summary>A live channel, wrapped for the list.</summary>
public sealed partial class LiveChannelViewModel(LiveChannel channel, bool isFavorite)
    : ObservableObject, IScrollKeyed
{
    public LiveChannel Channel { get; } = channel;

    public string Id => Channel.Id;
    public string Name => Channel.Name;
    public string? IconUrl => Channel.IconUrl;
    public string ScrollKey => Channel.Id;

    public string NumberLabel => Channel.Number?.ToString() ?? string.Empty;

    [ObservableProperty]
    private bool _isFavorite = isFavorite;

    /// <summary>Filled in from the EPG once it is loaded for this channel.</summary>
    [ObservableProperty]
    private string? _nowPlaying;

    [ObservableProperty]
    private string? _nextUp;

    [ObservableProperty]
    private double _programmeProgress;

    public bool HasArchive => Channel.HasArchive;
}

/// <summary>
/// The live channel browser: category rail, channel list, and a now/next panel.
/// </summary>
public sealed partial class LivePageViewModel : BrowsePageViewModel<LiveChannelViewModel>
{
    private readonly ICatalogStore _catalog;
    private readonly IXtreamClientFactory _clients;
    private readonly IProfileRepository _profiles;
    private readonly ISettingsRepository _settings;
    private readonly INavigationService _navigation;
    private readonly ILogger<LivePageViewModel> _log;

    private readonly Dictionary<string, IReadOnlyList<EpgEntry>> _epgCache =
        new(StringComparer.Ordinal);

    private CancellationTokenSource? _epgLoading;
    private Profile? _profile;

    [ObservableProperty]
    private IReadOnlyList<EpgEntry> _guide = [];

    [ObservableProperty]
    private bool _isGuideLoading;

    [ObservableProperty]
    private string? _guideMessage;

    public LivePageViewModel(
        ICatalogStore catalog,
        IXtreamClientFactory clients,
        IProfileRepository profiles,
        IFavoritesStore favorites,
        IHiddenCategoryRepository hidden,
        ISettingsRepository settings,
        INavigationService navigation,
        ILogger<LivePageViewModel> log)
        : base(favorites, hidden, settings, catalog)
    {
        _catalog = catalog;
        _clients = clients;
        _profiles = profiles;
        _settings = settings;
        _navigation = navigation;
        _log = log;
    }

    public override PageKey Key => PageKey.Live;

    public override string Title => "Live TV";

    protected override ContentKind Kind => ContentKind.Live;

    protected override CategoryScope Scope => CategoryScope.Live;

    protected override FilterableItem Project(LiveChannelViewModel item, int index) =>
        new(item.Id, item.Name, item.Channel.CategoryId, item.Channel.CategoryOrder, null, index);

    protected override string ItemIdOf(LiveChannelViewModel item) => item.Id;

    protected override string TitleOf(LiveChannelViewModel item) => item.Name;

    protected override string ListScrollKey => "channels";

    public override async Task OnNavigatedToAsync(
        object? parameter, bool isNewInstance, CancellationToken ct = default)
    {
        // Loading only on a first visit is the point of the cached stack:
        // returning must be instant, not a re-fetch.
        if (!isNewInstance)
        {
            return;
        }

        await ReloadFromCatalogAsync().ConfigureAwait(false);
    }

    protected override async Task ReloadFromCatalogAsync()
    {
        IsBusy = true;
        try
        {
            var profiles = await _profiles.ListAsync().ConfigureAwait(true);
            var activeId = await _settings.GetAsync(SettingKeys.ActiveProfileId).ConfigureAwait(true);

            _profile = profiles.FirstOrDefault(p => p.Id == activeId) ?? profiles.FirstOrDefault();

            if (_profile is null)
            {
                GuideMessage = "No provider configured.";
                return;
            }

            var channels = _catalog.Current.Live;

            var wrapped = new List<LiveChannelViewModel>(channels.Items.Count);
            foreach (var channel in channels.Items)
            {
                wrapped.Add(new LiveChannelViewModel(channel, IsFavorite(channel.Id)));
            }

            await SetSourceAsync(wrapped, channels.Categories, new ProfileScope(_profile.Id))
                .ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected override void OnItemSelected(LiveChannelViewModel? value)
    {
        if (value is not null)
        {
            _ = LoadGuideAsync(value);
        }
        else
        {
            Guide = [];
        }
    }

    /// <summary>
    /// Loads now/next for the selected channel.
    /// </summary>
    /// <remarks>
    /// Cancelled and replaced as the selection moves: arrowing down a channel
    /// list would otherwise fire a request per row and they would land out of
    /// order, showing the wrong programme for the selected channel.
    /// </remarks>
    private async Task LoadGuideAsync(LiveChannelViewModel channel)
    {
        _epgLoading?.Cancel();
        _epgLoading?.Dispose();

        if (_epgCache.TryGetValue(channel.Id, out var cached))
        {
            Guide = cached;
            ApplyNowNext(channel, cached);
            return;
        }

        if (_profile is null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _epgLoading = cts;

        Guide = [];
        GuideMessage = null;
        IsGuideLoading = true;

        try
        {
            // A short delay debounces arrow-key scrolling through the list.
            await Task.Delay(180, cts.Token).ConfigureAwait(true);

            var entries = await _clients.For(_profile)
                .GetShortEpgAsync(channel.Id, limit: 6, cts.Token)
                .ConfigureAwait(true);

            if (cts.IsCancellationRequested)
            {
                return;
            }

            _epgCache[channel.Id] = entries;
            Guide = entries;
            ApplyNowNext(channel, entries);

            GuideMessage = entries.Count == 0 ? "No guide data for this channel." : null;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection.
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Short EPG failed for {Channel}.", channel.Id);
            GuideMessage = "Guide unavailable.";
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                IsGuideLoading = false;
            }
        }
    }

    private static void ApplyNowNext(LiveChannelViewModel channel, IReadOnlyList<EpgEntry> entries)
    {
        var now = DateTimeOffset.Now;

        var current = entries.FirstOrDefault(e => e.IsNowPlaying(now));
        var next = entries.FirstOrDefault(e => e.Start > now);

        channel.NowPlaying = current?.Title;
        channel.NextUp = next?.Title;

        channel.ProgrammeProgress = current is null
            ? 0
            : Math.Clamp(
                (now - current.Start).TotalSeconds / (current.Stop - current.Start).TotalSeconds,
                0, 1);
    }

    [RelayCommand]
    private async Task PlayAsync(LiveChannelViewModel? channel)
    {
        channel ??= SelectedItem;
        if (channel is null)
        {
            return;
        }

        await _navigation.NavigateAsync(new NavRoute(PageKey.Player, new PlaybackRequestParameter(
            channel.Channel.StreamUrl,
            channel.Name,
            ContentKind.Live,
            channel.Id))).ConfigureAwait(false);
    }
}

/// <summary>What the player page needs to start playing something.</summary>
public sealed record PlaybackRequestParameter(
    string Url,
    string Title,
    ContentKind Kind,
    string ContentId,
    TimeSpan? StartAt = null,
    string? Subtitle = null,
    IReadOnlyList<PlaylistEntryParameter>? Playlist = null,
    int PlaylistIndex = 0);

public sealed record PlaylistEntryParameter(
    string ContentId,
    string Title,
    string Url,
    int? SeasonNumber,
    int? EpisodeNumber);
