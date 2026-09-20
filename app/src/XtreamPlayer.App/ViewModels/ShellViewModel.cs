using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using XtreamPlayer.Core.Abstractions;
using XtreamPlayer.Core.Library;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Core.Navigation;
using XtreamPlayer.Infrastructure.Migration;
using XtreamPlayer.Infrastructure.Persistence;
using XtreamPlayer.Infrastructure.Xtream;

namespace XtreamPlayer.App.ViewModels;

public sealed record NavDestination(PageKey Key, string Label, string Glyph);

/// <summary>
/// The application shell: sidebar, breadcrumbs, back/forward, and the page host.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly ICatalogStore _catalog;
    private readonly IProfileRepository _profiles;
    private readonly IFavoritesStore _favorites;
    private readonly ISettingsRepository _settings;
    private readonly IDbConnectionFactory _connections;
    private readonly LegacyImporter _importer;
    private readonly ILogger<ShellViewModel> _log;

    [ObservableProperty]
    private PageViewModelBase? _currentPage;

    [ObservableProperty]
    private Profile? _activeProfile;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isSyncing;

    [ObservableProperty]
    private string? _quarantineNotice;

    [ObservableProperty]
    private bool _needsSetup;

    [ObservableProperty]
    private string _globalSearch = string.Empty;

    public ShellViewModel(
        INavigationService navigation,
        ICatalogStore catalog,
        IProfileRepository profiles,
        IFavoritesStore favorites,
        ISettingsRepository settings,
        IDbConnectionFactory connections,
        LegacyImporter importer,
        ILogger<ShellViewModel> log)
    {
        _navigation = navigation;
        _catalog = catalog;
        _profiles = profiles;
        _favorites = favorites;
        _settings = settings;
        _connections = connections;
        _importer = importer;
        _log = log;

        _navigation.Navigated += (_, _) =>
        {
            CurrentPage = _navigation.Current;
            RefreshNavigationState();
        };

        _catalog.Progress += (_, progress) => StatusMessage = progress.Message;

        WeakReferenceMessenger.Default.Register<FullscreenChangedMessage>(this,
            (_, message) => Avalonia.Threading.Dispatcher.UIThread.Post(
                () => IsFullscreen = message.IsFullscreen));

        WeakReferenceMessenger.Default.Register<ProfilesChangedMessage>(this,
            (_, message) => _ = OnProfilesChangedAsync(message));
    }

    public ObservableCollection<Profile> Profiles { get; } = [];

    public ObservableCollection<LegacyDatabaseInfo> LegacyDatabases { get; } = [];

    /// <summary>
    /// Hides every piece of shell chrome so the video owns the whole window.
    /// </summary>
    [ObservableProperty]
    private bool _isFullscreen;

    /// <summary>The primary sidebar destinations, in the order they are used.</summary>
    public IReadOnlyList<NavDestination> Destinations { get; } =
    [
        new(PageKey.Dashboard, "Home", ""),
        new(PageKey.Live, "Live TV", ""),
        new(PageKey.Guide, "Guide", ""),
        new(PageKey.Vod, "Movies", ""),
        new(PageKey.Series, "Series", ""),
        new(PageKey.Downloads, "Downloads", ""),
    ];

    public IReadOnlyList<NavCrumb> Breadcrumbs => _navigation.Breadcrumbs;

    public bool CanGoBack => _navigation.CanGoBack;

    public bool CanGoForward => _navigation.CanGoForward;

    public bool HasLegacyDatabases => LegacyDatabases.Count > 0;

    public string CatalogSummary => _catalog.Current.IsEmpty
        ? "No catalog loaded"
        : $"{_catalog.Current.Live.Items.Count:N0} channels · "
        + $"{_catalog.Current.Vod.Items.Count:N0} movies · "
        + $"{_catalog.Current.Series.Items.Count:N0} series";

    private void RefreshNavigationState()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(Breadcrumbs));
    }

    // ---- startup --------------------------------------------------------

    public async Task InitializeAsync()
    {
        if (_connections is SqliteConnectionFactory { Quarantined: { } quarantined })
        {
            QuarantineNotice =
                $"An existing database here was set aside because {quarantined.Reason}. "
              + $"Nothing was deleted — it is at {quarantined.BackupPath}.";

            _log.LogWarning("Quarantined a database: {Reason}", quarantined.Reason);
        }

        await LoadProfilesAsync().ConfigureAwait(true);

        if (ActiveProfile is null)
        {
            NeedsSetup = true;

            var alreadyImported = await _settings.GetAsync(SettingKeys.MigrationV1Completed)
                .ConfigureAwait(true);

            if (alreadyImported is null)
            {
                foreach (var candidate in _importer.DiscoverLegacyDatabases())
                {
                    LegacyDatabases.Add(candidate);
                }

                OnPropertyChanged(nameof(HasLegacyDatabases));
            }

            await _navigation.NavigateAsync(new NavRoute(PageKey.Providers)).ConfigureAwait(true);
            return;
        }

        await _navigation.NavigateAsync(new NavRoute(PageKey.Dashboard)).ConfigureAwait(true);

        // Catalogs load after the first page is on screen, so the window is
        // never blank while a 26 MB payload downloads.
        _ = SyncAsync();
    }

    private async Task LoadProfilesAsync()
    {
        Profiles.Clear();

        var all = await _profiles.ListAsync().ConfigureAwait(true);
        foreach (var profile in all)
        {
            Profiles.Add(profile);
        }

        var activeId = await _settings.GetAsync(SettingKeys.ActiveProfileId).ConfigureAwait(true);
        ActiveProfile = all.FirstOrDefault(p => p.Id == activeId) ?? all.FirstOrDefault();

        if (ActiveProfile is not null)
        {
            await _favorites.LoadAsync(new ProfileScope(ActiveProfile.Id)).ConfigureAwait(true);
        }
    }

    /// <summary>The toolbar refresh button, which always forces a refetch.</summary>
    /// <remarks>
    /// Separate from SyncAsync because a RelayCommand&lt;bool&gt; bound to
    /// CommandParameter="True" receives the string "True" and throws during
    /// the layout pass, which is fatal.
    /// </remarks>
    [RelayCommand]
    private Task ForceSyncAsync() => SyncAsync(force: true);

    public async Task SyncAsync(bool force = false)
    {
        if (ActiveProfile is null || IsSyncing)
        {
            return;
        }

        IsSyncing = true;
        try
        {
            await _catalog.LoadAsync(ActiveProfile, force).ConfigureAwait(true);

            StatusMessage = CatalogSummary;
            OnPropertyChanged(nameof(CatalogSummary));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Catalog sync failed.");
            StatusMessage = $"Sync failed: {ex.Message}";
        }
        finally
        {
            IsSyncing = false;
        }
    }

    /// <summary>
    /// Picks up a provider added, imported or deleted on the Providers page.
    /// </summary>
    private async Task OnProfilesChangedAsync(ProfilesChangedMessage message)
    {
        await LoadProfilesAsync().ConfigureAwait(true);

        if (ActiveProfile is null)
        {
            NeedsSetup = true;
            return;
        }

        NeedsSetup = false;
        LegacyDatabases.Clear();
        OnPropertyChanged(nameof(HasLegacyDatabases));

        // A newly usable provider means the whole stack is about the old one.
        if (message.ActivateProfileId is not null)
        {
            await _navigation.ResetToAsync(new NavRoute(PageKey.Dashboard)).ConfigureAwait(true);
        }

        await SyncAsync().ConfigureAwait(true);
    }

    // ---- navigation -----------------------------------------------------

    [RelayCommand]
    public async Task GoToAsync(PageKey key) =>
        await _navigation.NavigateAsync(new NavRoute(key)).ConfigureAwait(true);

    [RelayCommand]
    public async Task GoBackAsync() => await _navigation.GoBackAsync().ConfigureAwait(true);

    [RelayCommand]
    public async Task GoForwardAsync() => await _navigation.GoForwardAsync().ConfigureAwait(true);

    [RelayCommand]
    public async Task PopToAsync(NavCrumb? crumb)
    {
        if (crumb is not null)
        {
            await _navigation.PopToIndexAsync(crumb.Index).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Switching provider discards the whole navigation stack.
    /// </summary>
    /// <remarks>
    /// Every cached page holds items from the previous provider's catalog, and
    /// content ids are not comparable across providers. The old app cleared the
    /// catalogs but left the current view pointing at a page whose contents no
    /// longer existed.
    /// </remarks>
    [RelayCommand]
    public async Task SelectProfileAsync(Profile? profile)
    {
        if (profile is null || profile.Id == ActiveProfile?.Id)
        {
            return;
        }

        ActiveProfile = profile;
        await _settings.SetAsync(SettingKeys.ActiveProfileId, profile.Id).ConfigureAwait(true);
        await _favorites.LoadAsync(new ProfileScope(profile.Id)).ConfigureAwait(true);

        _catalog.Clear();
        await _navigation.ResetToAsync(new NavRoute(PageKey.Dashboard)).ConfigureAwait(true);

        _ = SyncAsync();
    }

    [RelayCommand]
    private async Task ImportAsync(LegacyDatabaseInfo? source)
    {
        if (source is null)
        {
            return;
        }

        IsSyncing = true;
        try
        {
            var result = await _importer.ImportAsync(source.Path).ConfigureAwait(true);

            StatusMessage =
                $"Imported {result.ProfilesImported} provider(s) and "
              + $"{result.FavoritesImported} favorite(s).";

            LegacyDatabases.Clear();
            OnPropertyChanged(nameof(HasLegacyDatabases));

            await LoadProfilesAsync().ConfigureAwait(true);

            if (ActiveProfile is not null)
            {
                await _settings.SetAsync(SettingKeys.ActiveProfileId, ActiveProfile.Id)
                    .ConfigureAwait(true);

                NeedsSetup = false;
                await _navigation.ResetToAsync(new NavRoute(PageKey.Dashboard)).ConfigureAwait(true);
                _ = SyncAsync();
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Import failed.");
            StatusMessage = $"Import failed: {ex.Message}";
        }
        finally
        {
            IsSyncing = false;
        }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (GlobalSearch.Trim().Length < 2)
        {
            return;
        }

        // Search lands on the dashboard, which owns the cross-catalog results.
        await _navigation.NavigateAsync(new NavRoute(PageKey.Dashboard, GlobalSearch.Trim()))
            .ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task OpenSettingsAsync() =>
        await _navigation.NavigateAsync(new NavRoute(PageKey.Settings)).ConfigureAwait(true);
}
