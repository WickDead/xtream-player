using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;
using XtreamPlayer.Infrastructure.Persistence;

namespace XtreamPlayer.App.Media;

public interface IPosterCache
{
    /// <summary>Returns a decoded bitmap, or null if it cannot be fetched.</summary>
    Task<Bitmap?> GetAsync(string url, int decodeWidth, CancellationToken ct);

    /// <summary>Already in memory, so it can be shown with no flicker at all.</summary>
    bool TryGetCached(string url, out Bitmap? bitmap);
}

/// <summary>
/// Two-level poster cache: bounded memory over a disk store.
/// </summary>
/// <remarks>
/// <para>
/// This is the single biggest factor in whether a 68,595-item grid feels
/// smooth. Three things matter, in order:
/// </para>
/// <list type="number">
/// <item>Decode at display size. A 1000x1500 poster decoded full-size costs 6 MB
/// of memory to draw at 168 px wide.</item>
/// <item>Cancel decodes for tiles that have scrolled away. Without this, fast
/// scrolling queues thousands of decodes and the app stutters in a way that
/// looks like a virtualization problem and is not.</item>
/// <item>Collapse concurrent requests for the same URL. A grid re-realizing the
/// same rows would otherwise fetch each poster several times over.</item>
/// </list>
/// </remarks>
public sealed class PosterCache : IPosterCache, IDisposable
{
    /// <summary>
    /// Roughly a few screens' worth of tiles. Large enough that scrolling back
    /// is instant, small enough to stay well inside a sane memory budget.
    /// </summary>
    private const int MemoryCapacity = 400;

    private readonly HttpClient _http;
    private readonly ILogger<PosterCache> _log;

    private readonly ConcurrentDictionary<string, Bitmap> _memory = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _insertionOrder = new();

    /// <summary>In-flight fetches, so the same poster is never downloaded twice at once.</summary>
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _pending = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _networkLimit = new(6, 6);
    private bool _disposed;

    public PosterCache(IHttpClientFactory httpFactory, ILogger<PosterCache> log)
    {
        _http = httpFactory.CreateClient("posters");
        _log = log;

        Directory.CreateDirectory(DatabasePaths.PosterCacheDirectory);
    }

    public bool TryGetCached(string url, out Bitmap? bitmap)
    {
        if (!string.IsNullOrEmpty(url) && _memory.TryGetValue(url, out var found))
        {
            bitmap = found;
            return true;
        }

        bitmap = null;
        return false;
    }

    public Task<Bitmap?> GetAsync(string url, int decodeWidth, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return Task.FromResult<Bitmap?>(null);
        }

        if (_memory.TryGetValue(url, out var cached))
        {
            return Task.FromResult<Bitmap?>(cached);
        }

        // GetOrAdd rather than a lock: many tiles ask for the same poster at
        // once when a row re-realizes.
        //
        // The caller's token is deliberately NOT passed down. This task is
        // shared between every tile asking for the same URL, so honouring one
        // participant's cancellation kills the download for all of them - and
        // because AsyncImage restarts its load on attach, the very first tile
        // routinely cancels the request it just made. The symptom was requests
        // being sent and no response ever arriving, with nothing logged.
        //
        // The fetch runs to completion under its own timeout; the caller's token
        // only governs whether it waits for the result.
        return _pending.GetOrAdd(url, key => LoadAsync(key, decodeWidth));
    }

    private async Task<Bitmap?> LoadAsync(string url, int decodeWidth)
    {
        try
        {
            // A poster is never worth waiting on for long, but it must not be
            // abandoned just because one tile moved.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var ct = timeout.Token;

            var path = DiskPath(url);

            if (File.Exists(path))
            {
                var fromDisk = await DecodeAsync(path, decodeWidth, ct).ConfigureAwait(false);
                if (fromDisk is not null)
                {
                    Remember(url, fromDisk);
                    return fromDisk;
                }

                // Corrupt or truncated; fall through and refetch.
                TryDelete(path);
            }

            await _networkLimit.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var bytes = await _http.GetByteArrayAsync(url, ct).ConfigureAwait(false);

                // Dead poster hosts routinely answer 200 with an HTML error
                // page. Caching that means decoding junk now and every time the
                // tile is shown again, so reject it on the magic bytes.
                if (!LooksLikeAnImage(bytes))
                {
                    _log.LogDebug("Not an image, ignoring: {Url}", url);
                    return null;
                }

                await WriteAtomicallyAsync(path, bytes, ct).ConfigureAwait(false);

                var bitmap = await DecodeAsync(path, decodeWidth, ct).ConfigureAwait(false);
                if (bitmap is not null)
                {
                    Remember(url, bitmap);
                }

                return bitmap;
            }
            finally
            {
                _networkLimit.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Timed out. Common with dead poster hosts, and not worth a warning.
            return null;
        }
        catch (Exception ex)
        {
            // Total, and for the same reason as DecodeAsync: this task is shared
            // between every tile wanting the same poster, so anything escaping
            // here becomes an unobserved task exception on the finalizer thread.
            // No poster is worth that.
            _log.LogDebug(ex, "Poster unavailable: {Url}", url);
            return null;
        }
        finally
        {
            _pending.TryRemove(url, out _);
        }
    }

    /// <summary>
    /// The first bytes of the formats a poster can plausibly be.
    /// </summary>
    private static bool LooksLikeAnImage(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12)
        {
            return false;
        }

        // JPEG, PNG, GIF, BMP.
        if (bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            return true;
        }

        if (bytes is [0x89, (byte)'P', (byte)'N', (byte)'G', ..])
        {
            return true;
        }

        if (bytes is [(byte)'G', (byte)'I', (byte)'F', ..])
        {
            return true;
        }

        if (bytes is [(byte)'B', (byte)'M', ..])
        {
            return true;
        }

        // WebP is "RIFF????WEBP".
        return bytes is [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _,
                         (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..];
    }

    /// <summary>
    /// Decodes off the UI thread, at the size it will actually be drawn.
    /// </summary>
    /// <remarks>
    /// Catches everything, deliberately. Skia throws a raw
    /// NullReferenceException out of ImmutableBitmap's constructor when handed
    /// something it cannot decode, which is not a documented exception type and
    /// is not one a narrow catch list would ever include. It escaped as an
    /// unobserved task exception and was logged as fatal. A broken poster must
    /// never be able to do that.
    /// </remarks>
    private static Task<Bitmap?> DecodeAsync(string path, int decodeWidth, CancellationToken ct) =>
        Task.Run<Bitmap?>(() =>
        {
            try
            {
                using var stream = File.OpenRead(path);

                // DecodeToWidth is the whole point: a full-size decode of a
                // 1000x1500 poster costs ~6 MB to draw at 168 px.
                return Bitmap.DecodeToWidth(
                    stream, Math.Max(1, decodeWidth), BitmapInterpolationMode.HighQuality);
            }
            catch (Exception)
            {
                return null;
            }
        }, ct);

    private void Remember(string url, Bitmap bitmap)
    {
        if (!_memory.TryAdd(url, bitmap))
        {
            return;
        }

        _insertionOrder.Enqueue(url);

        // Approximate FIFO rather than true LRU. Tracking access order would
        // need a lock on the hot path, and for a grid the user scrolls through
        // in one direction the two behave almost identically.
        while (_memory.Count > MemoryCapacity && _insertionOrder.TryDequeue(out var oldest))
        {
            if (_memory.TryRemove(oldest, out var evicted))
            {
                evicted.Dispose();
            }
        }
    }

    private static async Task WriteAtomicallyAsync(string path, byte[] bytes, CancellationToken ct)
    {
        // A half-written file would decode as corrupt forever after.
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Another thread is reading it; it will be replaced next time.
        }
    }

    /// <summary>
    /// Content-addressed by URL hash: poster URLs are long, contain characters
    /// that are invalid in file names, and are not unique by basename.
    /// </summary>
    private static string DiskPath(string url)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)));

        // A two-character prefix directory keeps any single folder from holding
        // tens of thousands of entries, which Windows handles poorly.
        var directory = Path.Combine(DatabasePaths.PosterCacheDirectory, hash[..2]);
        Directory.CreateDirectory(directory);

        return Path.Combine(directory, hash[2..] + ".img");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var bitmap in _memory.Values)
        {
            bitmap.Dispose();
        }

        _memory.Clear();
        _networkLimit.Dispose();
    }
}
