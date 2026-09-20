using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using XtreamPlayer.Core.Library;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Infrastructure.Xtream;

namespace XtreamPlayer.Infrastructure.Downloads;

public sealed class DownloadChangedEventArgs(DownloadJob job) : EventArgs
{
    public DownloadJob Job { get; } = job;
}

public interface IDownloadManager
{
    IReadOnlyList<DownloadJob> Jobs { get; }

    event EventHandler<DownloadChangedEventArgs>? Changed;

    Task LoadAsync(ProfileScope scope, CancellationToken ct = default);

    Task<DownloadJob> EnqueueAsync(
        ProfileScope scope, ContentKind kind, string contentId, string title,
        string sourceUrl, string targetPath, CancellationToken ct = default);

    Task PauseAsync(string id);
    Task ResumeAsync(string id);

    /// <summary>Stops the job and keeps the partial file, so it can be resumed.</summary>
    Task CancelAsync(string id);

    /// <summary>Stops the job and deletes everything it wrote.</summary>
    Task DeleteAsync(string id);

    /// <summary>
    /// Downloads compete with playback for the provider's connection slots, so
    /// the player tells the manager when a stream is open.
    /// </summary>
    void SetPlaybackActive(bool active);
}

/// <summary>
/// A resumable download queue.
/// </summary>
/// <remarks>
/// <para>
/// Concurrency is deliberately tiny: one download while something is playing,
/// two otherwise. Every concurrent transfer consumes one of the provider's
/// connection slots — commonly two in total — and exceeding the limit does not
/// merely slow the download, it breaks the stream the user is watching.
/// </para>
/// <para>
/// The queue lives in the database, so it survives a restart and resumes from
/// the actual length of the partial file rather than whatever the last progress
/// write said.
/// </para>
/// </remarks>
public sealed class DownloadManager(
    IHttpClientFactory httpFactory,
    IDownloadRepository repository,
    ILogger<DownloadManager> log) : IDownloadManager, IAsyncDisposable
{
    /// <summary>Progress is written this often, not on every buffer.</summary>
    private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(5);

    private const int BufferSize = 128 * 1024;

    private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _pump = new(1, 1);

    private SemaphoreSlim _slots = new(2, 2);
    private bool _playbackActive;
    private ProfileScope? _scope;
    private bool _disposed;

    public IReadOnlyList<DownloadJob> Jobs =>
        [.. _jobs.Values.OrderByDescending(j => j.CreatedAt)];

    public event EventHandler<DownloadChangedEventArgs>? Changed;

    public async Task LoadAsync(ProfileScope scope, CancellationToken ct = default)
    {
        _scope = scope;
        _jobs.Clear();

        foreach (var job in await repository.ListAsync(scope, ct).ConfigureAwait(false))
        {
            // Anything still marked active was interrupted by the app closing.
            // Trust the filesystem over the last progress write.
            var restored = job.IsActive
                ? job with
                {
                    State = DownloadState.Queued,
                    DownloadedBytes = FileLength(job.PartialPath),
                }
                : job;

            _jobs[restored.Id] = restored;
        }

        _ = PumpAsync();
    }

    public void SetPlaybackActive(bool active)
    {
        if (_playbackActive == active)
        {
            return;
        }

        _playbackActive = active;

        // Rebuilt rather than adjusted: SemaphoreSlim cannot shrink, and an
        // in-flight download keeps its slot until it yields either way.
        _slots = new SemaphoreSlim(active ? 1 : 2, active ? 1 : 2);

        log.LogInformation(
            "Download concurrency now {Slots} ({Reason}).",
            active ? 1 : 2, active ? "playback active" : "idle");
    }

    public async Task<DownloadJob> EnqueueAsync(
        ProfileScope scope, ContentKind kind, string contentId, string title,
        string sourceUrl, string targetPath, CancellationToken ct = default)
    {
        _scope = scope;

        // Re-queueing something already downloaded should not start again.
        var existing = await repository.FindAsync(scope, contentId, kind, ct).ConfigureAwait(false);

        if (existing is not null && existing.State == DownloadState.Completed
            && File.Exists(existing.TargetPath))
        {
            return existing;
        }

        var job = existing is not null
            ? existing with
            {
                State = DownloadState.Queued,
                Error = null,
                DownloadedBytes = FileLength(existing.PartialPath),
                UpdatedAt = DateTimeOffset.UtcNow,
            }
            : new DownloadJob
            {
                Id = Guid.NewGuid().ToString(),
                ProfileId = scope.Value,
                Kind = kind,
                ContentId = contentId,
                Title = title,
                SourceUrl = sourceUrl,
                TargetPath = targetPath,
                State = DownloadState.Queued,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

        Directory.CreateDirectory(Path.GetDirectoryName(job.TargetPath)!);

        await UpdateAsync(job, ct).ConfigureAwait(false);
        _ = PumpAsync();

        return job;
    }

    public async Task PauseAsync(string id)
    {
        if (_running.TryRemove(id, out var cts))
        {
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
        }

        if (_jobs.TryGetValue(id, out var job))
        {
            await UpdateAsync(job with
            {
                State = DownloadState.Paused,
                DownloadedBytes = FileLength(job.PartialPath),
                UpdatedAt = DateTimeOffset.UtcNow,
            }).ConfigureAwait(false);
        }
    }

    public async Task ResumeAsync(string id)
    {
        if (!_jobs.TryGetValue(id, out var job))
        {
            return;
        }

        await UpdateAsync(job with
        {
            State = DownloadState.Queued,
            Error = null,
            // The file is the truth: a crash can lose the last progress write.
            DownloadedBytes = FileLength(job.PartialPath),
            UpdatedAt = DateTimeOffset.UtcNow,
        }).ConfigureAwait(false);

        _ = PumpAsync();
    }

    public async Task CancelAsync(string id)
    {
        if (_running.TryRemove(id, out var cts))
        {
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
        }

        if (_jobs.TryGetValue(id, out var job))
        {
            // The .part file stays, so cancelling is not destructive.
            await UpdateAsync(job with
            {
                State = DownloadState.Cancelled,
                DownloadedBytes = FileLength(job.PartialPath),
                UpdatedAt = DateTimeOffset.UtcNow,
            }).ConfigureAwait(false);
        }
    }

    public async Task DeleteAsync(string id)
    {
        await CancelAsync(id).ConfigureAwait(false);

        if (_jobs.TryRemove(id, out var job))
        {
            TryDelete(job.PartialPath);

            if (job.State != DownloadState.Completed)
            {
                TryDelete(job.TargetPath);
            }

            await repository.DeleteAsync(id).ConfigureAwait(false);
            Changed?.Invoke(this, new DownloadChangedEventArgs(job));
        }
    }

    // ---- the queue -------------------------------------------------------

    /// <summary>
    /// Starts whatever the slot budget allows.
    /// </summary>
    private async Task PumpAsync()
    {
        if (!await _pump.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            foreach (var job in _jobs.Values
                         .Where(j => j.State == DownloadState.Queued)
                         .OrderBy(j => j.CreatedAt))
            {
                if (_running.ContainsKey(job.Id))
                {
                    continue;
                }

                var cts = new CancellationTokenSource();

                if (!_running.TryAdd(job.Id, cts))
                {
                    cts.Dispose();
                    continue;
                }

                _ = RunAsync(job.Id, cts.Token);
            }
        }
        finally
        {
            _pump.Release();
        }
    }

    private async Task RunAsync(string id, CancellationToken ct)
    {
        // The slot is taken here rather than in the pump, so queued jobs sit
        // waiting cheaply instead of holding a thread each.
        var slots = _slots;
        await slots.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (!_jobs.TryGetValue(id, out var job) || job.State != DownloadState.Queued)
            {
                return;
            }

            await DownloadAsync(job, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Paused, cancelled, or shutting down; state is already recorded.
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Download failed: {Id}", id);

            if (_jobs.TryGetValue(id, out var job))
            {
                await UpdateAsync(job with
                {
                    State = DownloadState.Failed,
                    Error = ex.Message,
                    UpdatedAt = DateTimeOffset.UtcNow,
                }).ConfigureAwait(false);
            }
        }
        finally
        {
            slots.Release();

            if (_running.TryRemove(id, out var cts))
            {
                cts.Dispose();
            }

            _ = PumpAsync();
        }
    }

    private async Task DownloadAsync(DownloadJob job, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(XtreamClientFactory.HttpClientName);

        var existing = FileLength(job.PartialPath);
        var resuming = job.SupportsRange && existing > 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, job.SourceUrl);

        if (resuming)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
        }

        // Reassigned, not just persisted: every later progress write is built
        // from this local, so dropping the new state here would keep stamping the
        // row back to Queued while the transfer was plainly running.
        job = job with
        {
            State = DownloadState.Downloading,
            DownloadedBytes = existing,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await UpdateAsync(job, ct).ConfigureAwait(false);

        using var response = await http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        // The partial file is already the whole file: the transfer finished but
        // the app died before the rename. Asking for bytes past the end is a 416,
        // which would otherwise look like a hard failure on every retry.
        if (resuming && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            log.LogInformation("{Title}: already fully downloaded, finishing.", job.Title);

            File.Move(job.PartialPath, job.TargetPath, overwrite: true);

            await UpdateAsync(job with
            {
                State = DownloadState.Completed,
                DownloadedBytes = existing,
                TotalBytes = existing,
                UpdatedAt = DateTimeOffset.UtcNow,
            }, ct).ConfigureAwait(false);

            return;
        }

        response.EnsureSuccessStatusCode();

        var append = resuming;

        // A server that ignores Range answers 200 with the whole file. Appending
        // that to an existing partial would silently corrupt it, so start over
        // and stop asking for ranges from this URL.
        if (resuming && response.StatusCode != HttpStatusCode.PartialContent)
        {
            log.LogInformation(
                "{Title}: the server ignored Range, restarting from zero.", job.Title);

            append = false;
            existing = 0;

            job = job with { SupportsRange = false, DownloadedBytes = 0 };
            await UpdateAsync(job, ct).ConfigureAwait(false);
        }

        var total = response.Content.Headers.ContentLength is { } length
            ? length + (append ? existing : 0)
            : job.TotalBytes;

        job = job with { TotalBytes = total };

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        await using (var target = new FileStream(
            job.PartialPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.Read, BufferSize, useAsync: true))
        {
            var buffer = new byte[BufferSize];
            var written = existing;
            var lastPersist = DateTimeOffset.UtcNow;

            while (true)
            {
                int read;

                try
                {
                    read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Paused or cancelled mid-transfer. The bytes already written
                    // are real, so record them before unwinding rather than
                    // leaving the row showing whatever the last 5-second write
                    // happened to catch.
                    _jobs[job.Id] = job with { DownloadedBytes = written };
                    throw;
                }

                if (read == 0)
                {
                    break;
                }

                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                written += read;

                // Throttled: a 4 GB movie is 32,000 buffers, and writing a row
                // per buffer would cost more than the download.
                if (DateTimeOffset.UtcNow - lastPersist >= PersistInterval)
                {
                    lastPersist = DateTimeOffset.UtcNow;

                    job = job with { DownloadedBytes = written, UpdatedAt = lastPersist };
                    await UpdateAsync(job, ct).ConfigureAwait(false);
                }
            }

            job = job with { DownloadedBytes = written };
        }

        // Only now is the file complete, so only now does it get its real name.
        File.Move(job.PartialPath, job.TargetPath, overwrite: true);

        await UpdateAsync(job with
        {
            State = DownloadState.Completed,
            TotalBytes = job.DownloadedBytes,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, ct).ConfigureAwait(false);

        log.LogInformation("Downloaded {Title} to {Path}.", job.Title, job.TargetPath);
    }

    private async Task UpdateAsync(DownloadJob job, CancellationToken ct = default)
    {
        _jobs[job.Id] = job;

        try
        {
            await repository.SaveAsync(job, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Losing a progress row is recoverable; the file length is the truth.
            log.LogDebug(ex, "Could not persist download progress.");
        }

        Changed?.Invoke(this, new DownloadChangedEventArgs(job));
    }

    private static long FileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Still open somewhere; it will be replaced or cleaned up later.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var (_, cts) in _running)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
        }

        _running.Clear();
        _pump.Dispose();
    }
}
