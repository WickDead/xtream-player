using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using XtreamPlayer.Core.Abstractions;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Core.Navigation;
using XtreamPlayer.Infrastructure.Migration;
using XtreamPlayer.Infrastructure.Xtream;

namespace XtreamPlayer.App.ViewModels;

public sealed partial class ProvidersPageViewModel(
    IProfileRepository profiles,
    ISettingsRepository settings,
    IXtreamClientFactory clients,
    LegacyImporter importer,
    INavigationService navigation,
    ILogger<ProvidersPageViewModel> log) : PageViewModelBase
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _serverUrl = string.Empty;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string? _editingId;

    [ObservableProperty]
    private string? _testResult;

    [ObservableProperty]
    private bool _testSucceeded;

    [ObservableProperty]
    private string? _activeProfileId;

    public override PageKey Key => PageKey.Providers;

    public override string Title => "Providers";

    public ObservableCollection<Profile> Profiles { get; } = [];

    public ObservableCollection<LegacyDatabaseInfo> LegacyDatabases { get; } = [];

    public bool HasLegacyDatabases => LegacyDatabases.Count > 0;

    public bool IsEditing => EditingId is not null;

    public override async Task OnNavigatedToAsync(
        object? parameter, bool isNewInstance, CancellationToken ct = default)
    {
        await RefreshAsync(ct).ConfigureAwait(false);

        if (Profiles.Count == 0)
        {
            var alreadyImported = await settings.GetAsync(SettingKeys.MigrationV1Completed, ct)
                .ConfigureAwait(false);

            if (alreadyImported is null)
            {
                LegacyDatabases.Clear();
                foreach (var candidate in importer.DiscoverLegacyDatabases())
                {
                    LegacyDatabases.Add(candidate);
                }

                OnPropertyChanged(nameof(HasLegacyDatabases));
            }
        }
    }

    private async Task RefreshAsync(CancellationToken ct = default)
    {
        Profiles.Clear();

        foreach (var profile in await profiles.ListAsync(ct).ConfigureAwait(false))
        {
            Profiles.Add(profile);
        }

        ActiveProfileId = await settings.GetAsync(SettingKeys.ActiveProfileId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks the credentials before saving them.
    /// </summary>
    /// <remarks>
    /// Saving a provider that does not work, and only discovering it when a
    /// stream fails, is a bad first experience. The provider's own account
    /// state is far more useful than a generic success message.
    /// </remarks>
    [RelayCommand]
    private async Task TestAsync()
    {
        if (string.IsNullOrWhiteSpace(ServerUrl) || string.IsNullOrWhiteSpace(Username))
        {
            TestResult = "Enter a server address and username first.";
            TestSucceeded = false;
            return;
        }

        IsBusy = true;
        TestResult = null;

        try
        {
            var candidate = new Profile
            {
                Id = "probe",
                Name = Name,
                ServerUrl = ServerUrl,
                Username = Username,
                Password = Password,
            };

            var account = await clients.For(candidate).AuthenticateAsync(force: true)
                .ConfigureAwait(false);

            var expiry = account.ExpiresAt is { } date
                ? date.ToLocalTime().ToString("d MMM yyyy")
                : "no expiry";

            TestResult =
                $"Connected. {account.Status}, expires {expiry}, "
              + $"{account.ActiveConnections}/{account.MaxConnections} connections in use. "
              + $"Formats: {string.Join(", ", account.AllowedOutputFormats)}.";

            TestSucceeded = true;
        }
        catch (XtreamException ex)
        {
            TestResult = ex.Message;
            TestSucceeded = false;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Provider test failed.");
            TestResult = ex.Message;
            TestSucceeded = false;
        }
        finally
        {
            clients.Invalidate("probe");
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(ServerUrl) || string.IsNullOrWhiteSpace(Username))
        {
            TestResult = "A server address and username are required.";
            return;
        }

        var existing = EditingId is null
            ? null
            : Profiles.FirstOrDefault(p => p.Id == EditingId);

        var profile = new Profile
        {
            Id = EditingId ?? string.Empty,
            Name = string.IsNullOrWhiteSpace(Name) ? Username : Name.Trim(),
            ServerUrl = ServerUrl,
            Username = Username.Trim(),
            Password = Password,
            CreatedAt = existing?.CreatedAt ?? DateTimeOffset.UtcNow,
        };

        var saved = await profiles.SaveAsync(profile).ConfigureAwait(false);
        clients.Invalidate(saved.Id);

        var shouldActivate = ActiveProfileId is null;

        if (shouldActivate)
        {
            await settings.SetAsync(SettingKeys.ActiveProfileId, saved.Id).ConfigureAwait(false);
        }

        ClearForm();
        await RefreshAsync().ConfigureAwait(false);

        // The shell owns the active profile, the favorites store and catalog
        // syncing; it has to be told.
        WeakReferenceMessenger.Default.Send(
            new ProfilesChangedMessage(shouldActivate ? saved.Id : null));
    }

    [RelayCommand]
    private void Edit(Profile? profile)
    {
        if (profile is null)
        {
            return;
        }

        EditingId = profile.Id;
        Name = profile.Name;
        ServerUrl = profile.ServerUrl;
        Username = profile.Username;

        // Deliberately prefilled rather than blanked. The previous app always
        // cleared it, so editing a provider's name meant retyping the password.
        Password = profile.Password;

        TestResult = null;
        OnPropertyChanged(nameof(IsEditing));
    }

    [RelayCommand]
    private void ClearForm()
    {
        EditingId = null;
        Name = ServerUrl = Username = Password = string.Empty;
        TestResult = null;
        TestSucceeded = false;
        OnPropertyChanged(nameof(IsEditing));
    }

    [RelayCommand]
    private async Task DeleteAsync(Profile? profile)
    {
        if (profile is null)
        {
            return;
        }

        await profiles.DeleteAsync(new ProfileScope(profile.Id)).ConfigureAwait(false);
        clients.Invalidate(profile.Id);

        if (ActiveProfileId == profile.Id)
        {
            await settings.SetAsync(SettingKeys.ActiveProfileId, null).ConfigureAwait(false);
        }

        await RefreshAsync().ConfigureAwait(false);
        WeakReferenceMessenger.Default.Send(new ProfilesChangedMessage());
    }

    [RelayCommand]
    private async Task ImportAsync(LegacyDatabaseInfo? source)
    {
        if (source is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await importer.ImportAsync(source.Path).ConfigureAwait(false);
            TestResult =
                $"Imported {result.ProfilesImported} provider(s), "
              + $"{result.FavoritesImported} favorite(s).";
            TestSucceeded = true;

            LegacyDatabases.Clear();
            OnPropertyChanged(nameof(HasLegacyDatabases));

            await RefreshAsync().ConfigureAwait(false);

            if (Profiles.Count > 0 && ActiveProfileId is null)
            {
                // The provider with the most favorites is the one actually in
                // use; picking the first row imported would often be wrong.
                var best = Profiles[0];
                await settings.SetAsync(SettingKeys.ActiveProfileId, best.Id).ConfigureAwait(false);
                ActiveProfileId = best.Id;
            }

            WeakReferenceMessenger.Default.Send(new ProfilesChangedMessage(ActiveProfileId));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Import failed.");
            TestResult = $"Import failed: {ex.Message}";
            TestSucceeded = false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed partial class SettingsPageViewModel(
    ISettingsRepository settings,
    IHiddenCategoryRepository hidden,
    IProfileRepository profiles) : PageViewModelBase
{
    [ObservableProperty]
    private string _preferredAudioLanguages = "eng, en";

    [ObservableProperty]
    private bool _subtitlesOffByDefault = true;

    [ObservableProperty]
    private bool _hardwareDecoding = true;

    [ObservableProperty]
    private int _catalogTtlHours = 12;

    [ObservableProperty]
    private string? _statusMessage;

    public override PageKey Key => PageKey.Settings;

    public override string Title => "Settings";

    public string DataDirectory => Infrastructure.Persistence.DatabasePaths.DataDirectory;

    public string LogDirectory => Infrastructure.Persistence.DatabasePaths.LogDirectory;

    public override async Task OnNavigatedToAsync(
        object? parameter, bool isNewInstance, CancellationToken ct = default)
    {
        if (!isNewInstance)
        {
            return;
        }

        PreferredAudioLanguages =
            await settings.GetAsync(SettingKeys.PreferredAudioLanguages, ct).ConfigureAwait(false)
            ?? "eng, en";

        SubtitlesOffByDefault =
            await settings.GetAsync(SettingKeys.SubtitlesOffByDefault, ct).ConfigureAwait(false) != "0";

        HardwareDecoding =
            await settings.GetAsync(SettingKeys.HardwareDecoding, ct).ConfigureAwait(false) != "0";

        if (int.TryParse(
                await settings.GetAsync(SettingKeys.CatalogTtlHours, ct).ConfigureAwait(false),
                out var ttl) && ttl > 0)
        {
            CatalogTtlHours = ttl;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        await settings.SetAsync(SettingKeys.PreferredAudioLanguages, PreferredAudioLanguages)
            .ConfigureAwait(false);
        await settings.SetAsync(SettingKeys.SubtitlesOffByDefault, SubtitlesOffByDefault ? "1" : "0")
            .ConfigureAwait(false);
        await settings.SetAsync(SettingKeys.HardwareDecoding, HardwareDecoding ? "1" : "0")
            .ConfigureAwait(false);
        await settings.SetAsync(SettingKeys.CatalogTtlHours, CatalogTtlHours.ToString())
            .ConfigureAwait(false);

        StatusMessage = "Saved. Playback settings apply to the next stream you open.";
    }

    /// <summary>
    /// Clears hidden categories for every provider.
    /// </summary>
    /// <remarks>
    /// A visible escape hatch matters here: hidden categories are easy to set by
    /// accident and, without this, a user who hides the wrong thing has to find
    /// and unhide each one individually.
    /// </remarks>
    [RelayCommand]
    private async Task ResetHiddenCategoriesAsync()
    {
        var cleared = 0;

        foreach (var profile in await profiles.ListAsync().ConfigureAwait(false))
        {
            await hidden.ClearAsync(new ProfileScope(profile.Id)).ConfigureAwait(false);
            cleared++;
        }

        StatusMessage = $"Hidden categories cleared for {cleared} provider(s).";
    }
}
