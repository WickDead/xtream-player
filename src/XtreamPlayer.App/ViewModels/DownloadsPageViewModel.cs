using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using XtreamPlayer.Core.Abstractions;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Core.Navigation;
using XtreamPlayer.Infrastructure.Downloads;
using XtreamPlayer.Playback;

namespace XtreamPlayer.App.ViewModels;

/// <summary>One row in the queue.</summary>
public sealed partial class DownloadJobViewModel(DownloadJob job) : ObservableObject
{
    [ObservableProperty]
    private DownloadJob _job = job;

    public string Id => Job.Id;

    public string Title => Job.Title;

    public string TargetPath => Job.TargetPath;

    public double ProgressPercent => Job.Progress * 100;

    /// <summary>A server that sends no Content-Length leaves the bar indeterminate.</summary>
    public bool IsIndeterminate => Job.TotalBytes <= 0 && Job.State == DownloadState.Downloading;

    public bool CanPause => Job.State is DownloadState.Downloading or DownloadState.Queued;

    public bool CanResume => Job.State is DownloadState.Paused or DownloadState.Failed
        or DownloadState.Cancelled;

    public bool CanPlay => Job.State == DownloadState.Completed && File.Exists(Job.TargetPath);

    public string StatusText => Job.State switch
    {
        DownloadState.Completed => Size(Job.TotalBytes),
        DownloadState.Failed => Job.Error ?? "Failed",
        DownloadState.Paused => $"Paused - {Size(Job.DownloadedBytes)} of {Size(Job.TotalBytes)}",
        DownloadState.Cancelled => $"Stopped - {Size(Job.DownloadedBytes)} of {Size(Job.TotalBytes)}",
        DownloadState.Queued => "Waiting",
        _ => Job.TotalBytes > 0
            ? $"{Size(Job.DownloadedBytes)} of {Size(Job.TotalBytes)}"
            : Size(Job.DownloadedBytes),
    };

    public void Update(DownloadJob updated)
    {
        Job = updated;

        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(IsIndeterminate));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanPlay));
    }

    private static string Size(long bytes) => bytes switch
    {
        <= 0 => "-",
        < 1024L * 1024 => $"{bytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F0} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };
}

/// <summary>One recording in progress.</summary>
public sealed partial class RecordingViewModel(RecordingSession session) : ObservableObject
{
    public string Id => session.Id;

    public string ChannelName => session.ChannelName;

    public string? ProgrammeTitle => session.ProgrammeTitle;

    public string TargetPath => session.TargetPath;

    public string StatusText =>
        $"Recording - {session.Elapsed:hh\\:mm\\:ss}, {session.Bytes / (1024.0 * 1024):F0} MB";

    /// <summary>Driven by the page's timer; a recording has no progress events.</summary>
    public void Tick() => OnPropertyChanged(nameof(StatusText));
}

/// <summary>
/// The download queue and any recordings in progress.
/// </summary>
/// <remarks>
/// Both live here because they answer the same question — what is this app
/// writing to my disk right now — even though they use completely different
/// machinery underneath.
/// </remarks>
public sealed partial class DownloadsPageViewModel : PageViewModelBase
{
    private readonly IDownloadManager _downloads;
    private readonly IRecordingService _recordings;
    private readonly IProfileRepository _profiles;
    private readonly ISettingsRepository _settings;
    private readonly IDownloadPaths _paths;
    private readonly INavigationService _navigation;
    private readonly ILogger<DownloadsPageViewModel> _log;
    private readonly DispatcherTimer _timer;

    public DownloadsPageViewModel(
        IDownloadManager downloads,
        IRecordingService recordings,
        IProfileRepository profiles,
        ISettingsRepository settings,
        IDownloadPaths paths,
        INavigationService navigation,
        ILogger<DownloadsPageViewModel> log)
    {
        _downloads = downloads;
        _recordings = recordings;
        _profiles = profiles;
        _settings = settings;
        _paths = paths;
        _navigation = navigation;
        _log = log;

        // Recordings report no progress of their own, so the elapsed time and
        // file size are polled. Downloads push their own updates.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            foreach (var recording in Recordings)
            {
                recording.Tick();
            }
        };

        _downloads.Changed += OnDownloadChanged;
        _recordings.Changed += OnRecordingChanged;
    }

    [ObservableProperty]
    private string _folder = string.Empty;

    [ObservableProperty]
    private string? _status;

    public override PageKey Key => PageKey.Downloads;

    public override string Title => "Downloads";

    public ObservableCollection<DownloadJobViewModel> Jobs { get; } = [];

    public ObservableCollection<RecordingViewModel> Recordings { get; } = [];

    public bool HasJobs => Jobs.Count > 0;

    public bool HasRecordings => Recordings.Count > 0;

    public bool IsEmpty => Jobs.Count == 0 && Recordings.Count == 0;

    public override async Task OnNavigatedToAsync(
        object? parameter, bool isNewInstance, CancellationToken ct = default)
    {
        Folder = await _paths.RootAsync(ct).ConfigureAwait(true);

        var activeId = await _settings.GetAsync(SettingKeys.ActiveProfileId, ct).ConfigureAwait(true);
        var all = await _profiles.ListAsync(ct).ConfigureAwait(true);
        var profile = all.FirstOrDefault(p => p.Id == activeId) ?? all.FirstOrDefault();

        if (profile is not null)
        {
            await _downloads.LoadAsync(new ProfileScope(profile.Id), ct).ConfigureAwait(true);
        }

        Rebuild();
        _timer.Start();
    }

    public override Task OnNavigatingFromAsync(NavigationDirection direction)
    {
        _timer.Stop();
        return base.OnNavigatingFromAsync(direction);
    }

    private void Rebuild()
    {
        Jobs.Clear();

        foreach (var job in _downloads.Jobs)
        {
            Jobs.Add(new DownloadJobViewModel(job));
        }

        Recordings.Clear();

        foreach (var session in _recordings.Active)
        {
            Recordings.Add(new RecordingViewModel(session));
        }

        RaiseCounts();
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(HasJobs));
        OnPropertyChanged(nameof(HasRecordings));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void OnDownloadChanged(object? sender, DownloadChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            var existing = Jobs.FirstOrDefault(j => j.Id == e.Job.Id);

            if (existing is not null)
            {
                // Updated in place rather than replaced, so the row keeps its
                // selection and the progress bar animates instead of jumping.
                existing.Update(e.Job);
                return;
            }

            if (_downloads.Jobs.Any(j => j.Id == e.Job.Id))
            {
                Jobs.Insert(0, new DownloadJobViewModel(e.Job));
            }
            else
            {
                Jobs.Remove(Jobs.FirstOrDefault(j => j.Id == e.Job.Id)!);
            }

            RaiseCounts();
        });

    private void OnRecordingChanged(object? sender, RecordingChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (e.Started)
            {
                Recordings.Add(new RecordingViewModel(e.Session));
            }
            else
            {
                var match = Recordings.FirstOrDefault(r => r.Id == e.Session.Id);

                if (match is not null)
                {
                    Recordings.Remove(match);
                }
            }

            RaiseCounts();
        });

    [RelayCommand]
    private Task PauseAsync(DownloadJobViewModel? row) =>
        row is null ? Task.CompletedTask : _downloads.PauseAsync(row.Id);

    [RelayCommand]
    private Task ResumeAsync(DownloadJobViewModel? row) =>
        row is null ? Task.CompletedTask : _downloads.ResumeAsync(row.Id);

    [RelayCommand]
    private async Task RemoveAsync(DownloadJobViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        await _downloads.DeleteAsync(row.Id).ConfigureAwait(true);

        Jobs.Remove(row);
        RaiseCounts();
    }

    [RelayCommand]
    private async Task PlayAsync(DownloadJobViewModel? row)
    {
        if (row is null || !File.Exists(row.TargetPath))
        {
            Status = "That file is no longer on disk.";
            return;
        }

        // A downloaded file goes through the same player as a stream, so resume,
        // track switching and subtitles all behave identically.
        await _navigation.NavigateAsync(new NavRoute(PageKey.Player, new PlaybackRequestParameter(
            new Uri(row.TargetPath).AbsoluteUri,
            row.Title,
            row.Job.Kind,
            row.Job.ContentId))).ConfigureAwait(false);
    }

    [RelayCommand]
    private Task StopRecordingAsync(RecordingViewModel? row) =>
        row is null ? Task.CompletedTask : _recordings.StopAsync(row.Id);

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            Process.Start(new ProcessStartInfo(Folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not open the downloads folder.");
            Status = $"Could not open {Folder}.";
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _downloads.Changed -= OnDownloadChanged;
            _recordings.Changed -= OnRecordingChanged;
        }

        base.Dispose(disposing);
    }
}
