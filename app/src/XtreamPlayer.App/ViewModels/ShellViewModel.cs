using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using XtreamPlayer.Core.Abstractions;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Infrastructure.Migration;
using XtreamPlayer.Infrastructure.Persistence;
using XtreamPlayer.Playback;

namespace XtreamPlayer.App.ViewModels;

/// <summary>One provider row in the shell's provider list.</summary>
public sealed partial class ProfileRowViewModel(Profile profile, int favoriteCount) : ObservableObject
{
    public string Id => profile.Id;
    public string Name => profile.Name;
    public string ServerUrl => profile.ServerUrl;
    public string Username => profile.Username;
    public int FavoriteCount { get; } = favoriteCount;

    public string Status => profile.Status ?? "Unknown";

    public string Expiry => profile.ExpiryDate is { } date
        ? date.ToLocalTime().ToString("yyyy-MM-dd")
        : "No expiry";

    public string Connections => profile.MaxConnections is { } max ? $"{max} max" : "-";

    /// <summary>
    /// True when the stored password could not be decrypted, almost always
    /// because the database was copied from another machine or user account.
    /// </summary>
    public bool NeedsPassword => profile.PasswordUnavailable;
}

/// <summary>
/// The application shell.
/// </summary>
/// <remarks>
/// Phase 1 scope: prove the stack end to end — migrations run, repositories
/// read, credentials round-trip, and the legacy importer can find and describe
/// the previous app's data. Navigation and the real screens arrive in Phase 3.
/// </remarks>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IProfileRepository _profiles;
    private readonly IFavoritesRepository _favorites;
    private readonly ISettingsRepository _settings;
    private readonly IDbConnectionFactory _connections;
    private readonly LegacyImporter _importer;
    private readonly ILogger<ShellViewModel> _log;

    [ObservableProperty]
    private string _databasePath = string.Empty;

    [ObservableProperty]
    private int _schemaVersion;

    [ObservableProperty]
    private string _playbackEngineStatus = "Not checked";

    [ObservableProperty]
    private bool _playbackEngineAvailable;

    [ObservableProperty]
    private string _importStatus = string.Empty;

    /// <summary>
    /// Set when an incompatible pre-existing database was moved aside. Shown
    /// prominently: silently renaming someone's data file would be indefensible.
    /// </summary>
    [ObservableProperty]
    private string? _quarantineNotice;

    [ObservableProperty]
    private bool _isBusy;

    public ShellViewModel(
        IProfileRepository profiles,
        IFavoritesRepository favorites,
        ISettingsRepository settings,
        IDbConnectionFactory connections,
        LegacyImporter importer,
        ILogger<ShellViewModel> log)
    {
        _profiles = profiles;
        _favorites = favorites;
        _settings = settings;
        _connections = connections;
        _importer = importer;
        _log = log;
    }

    public ObservableCollection<ProfileRowViewModel> Profiles { get; } = [];

    public ObservableCollection<LegacyDatabaseInfo> LegacyDatabases { get; } = [];

    public bool HasProfiles => Profiles.Count > 0;

    public bool HasLegacyDatabases => LegacyDatabases.Count > 0;

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var factory = _connections as SqliteConnectionFactory;
            DatabasePath = factory?.DatabaseFile ?? DatabasePaths.DatabaseFile;

            if (factory?.Quarantined is { } quarantined)
            {
                QuarantineNotice =
                    $"An existing database at this location was set aside because {quarantined.Reason}. "
                  + $"Nothing was deleted - it is at {quarantined.BackupPath}. A new database was created.";

                _log.LogWarning(
                    "Quarantined an incompatible database: {Reason}. Moved to {Backup}.",
                    quarantined.Reason, quarantined.BackupPath);
            }

            // Opening the connection is what runs migrations, so the version
            // read here also proves the runner worked.
            await using (var connection = _connections.Open())
            {
                SchemaVersion = new SqliteMigrator().GetCurrentVersion(connection);
            }

            await RefreshProfilesAsync();

            LegacyDatabases.Clear();
            var alreadyImported = await _settings.GetAsync(SettingKeys.MigrationV1Completed);

            if (alreadyImported is null)
            {
                foreach (var candidate in _importer.DiscoverLegacyDatabases())
                {
                    LegacyDatabases.Add(candidate);
                }

                ImportStatus = LegacyDatabases.Count == 0
                    ? "No data from the previous version was found."
                    : $"Found {LegacyDatabases.Count} database(s) from the previous version.";
            }
            else
            {
                ImportStatus = $"Imported on {alreadyImported}.";
            }

            OnPropertyChanged(nameof(HasProfiles));
            OnPropertyChanged(nameof(HasLegacyDatabases));

            _log.LogInformation(
                "Shell loaded. schema=v{Version} profiles={Profiles}",
                SchemaVersion, Profiles.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load the shell.");
            ImportStatus = $"Failed to load: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshProfilesAsync()
    {
        Profiles.Clear();

        foreach (var profile in await _profiles.ListAsync())
        {
            var favorites = await _favorites.ListAsync(new ProfileScope(profile.Id));
            Profiles.Add(new ProfileRowViewModel(profile, favorites.Count));
        }
    }

    [RelayCommand]
    public async Task ImportAsync(LegacyDatabaseInfo? source)
    {
        if (source is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _importer.ImportAsync(source.Path);

            ImportStatus =
                $"Imported {result.ProfilesImported} provider(s), "
              + $"{result.FavoritesImported} favorite(s), "
              + $"{result.ResumeImported} resume entr(ies).";

            if (result.Warnings.Count > 0)
            {
                ImportStatus += $" {result.Warnings.Count} warning(s) - see the log.";

                foreach (var warning in result.Warnings)
                {
                    _log.LogWarning("Import: {Warning}", warning);
                }
            }

            LegacyDatabases.Clear();
            OnPropertyChanged(nameof(HasLegacyDatabases));

            await RefreshProfilesAsync();
            OnPropertyChanged(nameof(HasProfiles));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Import failed.");
            ImportStatus = $"Import failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Confirms the video engine can load, without starting playback.
    /// </summary>
    /// <remarks>
    /// Deliberately a command rather than something done at startup: creating
    /// the LibVLC instance costs time and loads 400-odd native plugins, and
    /// there is no reason to pay for that before the user plays anything.
    /// </remarks>
    [RelayCommand]
    public void CheckPlaybackEngine()
    {
        try
        {
            VlcLocator.Initialize();
            PlaybackEngineAvailable = true;
            PlaybackEngineStatus = $"libvlc ready ({VlcLocator.ResolvedDirectory})";
        }
        catch (VlcInitializationException ex)
        {
            PlaybackEngineAvailable = false;
            PlaybackEngineStatus = $"{ex.Message} {ex.Remedy}";
            _log.LogError(ex, "libvlc unavailable.");
        }
    }
}
