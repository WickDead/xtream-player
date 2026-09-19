using Avalonia;
using LibVLCSharp.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using XtreamPlayer.App.Logging;
using XtreamPlayer.Infrastructure.Persistence;
using XtreamPlayer.Playback;

namespace XtreamPlayer.App;

internal static class Program
{
    /// <summary>
    /// Resolved by <see cref="App"/> once the Avalonia lifetime is up.
    /// </summary>
    public static IHost? Host { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // A GUI app that dies during startup otherwise prints nothing anywhere
        // and simply looks like it never ran.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Crash.Report("Unhandled exception", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Crash.Report("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        var verbose = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);
        Log.Logger = SerilogSetup.Create(verbose);

        try
        {
            DatabasePaths.EnsureCreated();
            Log.Information("Data directory: {Directory}", DatabasePaths.DataDirectory);

            // Must run before AppBuilder on macOS; harmless to front-load
            // everywhere else. A failure here is not fatal — everything except
            // playback still works, and the player surfaces the remedy.
            try
            {
                VlcLocator.Initialize();
                Log.Information("libvlc loaded from {Path}", VlcLocator.ResolvedDirectory);
            }
            catch (VlcInitializationException ex)
            {
                Log.Error(ex, "libvlc unavailable. Playback will not work. {Remedy}", ex.Remedy);
            }

            Host = BuildHost(verbose);

            // Headless import, for first-run setup without the UI and for
            // verifying the importer against a real database.
            if (TryGetOption(args, "--import") is { } legacyPath)
            {
                return RunImport(Host, legacyPath);
            }

            // Refreshes the XMLTV guide and reports how well it matched, which
            // is the number that decides whether the guide is actually useful.
            if (args.Contains("--refresh-guide", StringComparer.OrdinalIgnoreCase))
            {
                return RunGuideRefresh(Host);
            }

            // Downloads a movie matching a search term, so the queue, resume and
            // range handling can be exercised against the real provider.
            if (TryGetOption(args, "--download") is { } downloadQuery)
            {
                return RunDownload(Host, downloadQuery,
                    int.TryParse(TryGetOption(args, "--seconds"), out var limit) ? limit : 20);
            }

            // Measures each interaction against the real catalogs, because
            // "feels fast" is not a claim worth making without numbers.
            if (args.Contains("--benchmark", StringComparer.OrdinalIgnoreCase))
            {
                var profile = ActiveProfile(Host.Services);

                if (profile is null)
                {
                    Console.Error.WriteLine("No provider configured.");
                    return 2;
                }

                return Benchmark.Run(Host.Services, profile);
            }

            // Records a live channel matching a search term for N seconds.
            if (TryGetOption(args, "--record") is { } recordQuery)
            {
                return RunRecord(Host, recordQuery,
                    int.TryParse(TryGetOption(args, "--seconds"), out var secs) ? secs : 20);
            }

            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Crash.Report("The app failed to start", ex);
            return 1;
        }
        finally
        {
            Host?.Dispose();
            Log.CloseAndFlush();
        }
    }

    private static string? TryGetOption(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int RunImport(IHost host, string legacyPath)
    {
        if (!File.Exists(legacyPath))
        {
            Console.Error.WriteLine($"No such database: {legacyPath}");
            return 2;
        }

        var importer = host.Services
            .GetRequiredService<XtreamPlayer.Infrastructure.Migration.LegacyImporter>();

        var settings = host.Services
            .GetRequiredService<XtreamPlayer.Core.Abstractions.ISettingsRepository>();

        var profiles = host.Services
            .GetRequiredService<XtreamPlayer.Core.Abstractions.IProfileRepository>();

        var favorites = host.Services
            .GetRequiredService<XtreamPlayer.Core.Abstractions.IFavoritesRepository>();

        var result = importer.ImportAsync(legacyPath).GetAwaiter().GetResult();

        Console.WriteLine(
            $"Imported {result.ProfilesImported} provider(s), "
          + $"{result.FavoritesImported} favorite(s), "
          + $"{result.ResumeImported} resume entr(ies). "
          + $"{result.DemoRowsSkipped} demo row(s) skipped.");

        foreach (var warning in result.Warnings)
        {
            Console.WriteLine($"  warning: {warning}");
        }

        // Activate the provider that actually has favorites, not just the first
        // row imported.
        var all = profiles.ListAsync().GetAwaiter().GetResult();
        XtreamPlayer.Core.Models.Profile? best = null;
        var bestCount = -1;

        foreach (var profile in all)
        {
            var count = favorites
                .ListAsync(new XtreamPlayer.Core.Models.ProfileScope(profile.Id))
                .GetAwaiter().GetResult().Count;

            Console.WriteLine($"  {profile.Name}: {count} favorite(s) at {profile.ServerUrl}");

            if (count > bestCount)
            {
                bestCount = count;
                best = profile;
            }
        }

        if (best is not null)
        {
            settings.SetAsync(
                XtreamPlayer.Core.Abstractions.SettingKeys.ActiveProfileId, best.Id)
                .GetAwaiter().GetResult();

            Console.WriteLine($"Active provider: {best.Name}");
        }

        return 0;
    }

    /// <summary>
    /// Downloads the first movie matching a search term, briefly.
    /// </summary>
    /// <remarks>
    /// Verifies the whole chain against a real provider — range requests,
    /// the .part file, progress persistence and resume — without needing the
    /// UI. It cancels after a few seconds and reports whether the partial file
    /// is resumable, which is the property that actually matters.
    /// </remarks>
    private static int RunDownload(IHost host, string query, int seconds)
    {
        var services = host.Services;

        var profile = ActiveProfile(services);

        if (profile is null)
        {
            Console.Error.WriteLine("No provider configured.");
            return 2;
        }

        var catalog = services.GetRequiredService<XtreamPlayer.Infrastructure.Xtream.ICatalogStore>();
        catalog.LoadAsync(profile).GetAwaiter().GetResult();

        var item = catalog.Current.Vod.Items.FirstOrDefault(i =>
            i.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

        if (item is null)
        {
            Console.Error.WriteLine($"No movie matching \"{query}\".");
            return 2;
        }

        var downloads = services.GetRequiredService<
            XtreamPlayer.Infrastructure.Downloads.IDownloadManager>();

        var paths = services.GetRequiredService<
            XtreamPlayer.Infrastructure.Downloads.IDownloadPaths>();

        var scope = new XtreamPlayer.Core.Models.ProfileScope(profile.Id);
        var target = paths.ForMovieAsync(item).GetAwaiter().GetResult();

        Console.WriteLine($"Downloading: {item.Name}");
        Console.WriteLine($"To         : {target}");

        downloads.LoadAsync(scope).GetAwaiter().GetResult();

        var job = downloads.EnqueueAsync(
            scope, XtreamPlayer.Core.Models.ContentKind.Vod, item.Id, item.Name,
            item.StreamUrl, target).GetAwaiter().GetResult();

        for (var i = 0; i < seconds; i++)
        {
            Thread.Sleep(1000);

            var current = downloads.Jobs.FirstOrDefault(j => j.Id == job.Id);

            if (current is null)
            {
                break;
            }

            Console.WriteLine(
                $"  {current.State}  {current.DownloadedBytes / (1024.0 * 1024):F1} MB"
              + (current.TotalBytes > 0 ? $" of {current.TotalBytes / (1024.0 * 1024):F1} MB" : string.Empty)
              + (current.Error is { } e ? $"  [{e}]" : string.Empty));

            if (current.State is XtreamPlayer.Core.Models.DownloadState.Completed
                or XtreamPlayer.Core.Models.DownloadState.Failed)
            {
                break;
            }
        }

        downloads.PauseAsync(job.Id).GetAwaiter().GetResult();
        Thread.Sleep(500);

        var final = downloads.Jobs.FirstOrDefault(j => j.Id == job.Id);

        // The job's own path, not the one just computed: re-queueing an existing
        // download keeps writing to the file it already started.
        var actual = final?.TargetPath ?? target;
        var partial = actual + ".part";

        Console.WriteLine();
        Console.WriteLine($"state      : {final?.State}");
        Console.WriteLine($"supports   : {(final?.SupportsRange == true ? "range (resumable)" : "no range")}");
        Console.WriteLine($"file       : {actual}");
        Console.WriteLine($"on disk    : {(File.Exists(partial)
            ? $"{new FileInfo(partial).Length / (1024.0 * 1024):F1} MB partial"
            : File.Exists(actual) ? "complete" : "nothing")}");

        return final is null || final.State == XtreamPlayer.Core.Models.DownloadState.Failed ? 1 : 0;
    }

    /// <summary>Records a matching live channel for a few seconds.</summary>
    private static int RunRecord(IHost host, string query, int seconds)
    {
        var services = host.Services;
        var profile = ActiveProfile(services);

        if (profile is null)
        {
            Console.Error.WriteLine("No provider configured.");
            return 2;
        }

        var catalog = services.GetRequiredService<XtreamPlayer.Infrastructure.Xtream.ICatalogStore>();
        catalog.LoadAsync(profile).GetAwaiter().GetResult();

        var channel = catalog.Current.Live.Items.FirstOrDefault(c =>
            c.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

        if (channel is null)
        {
            Console.Error.WriteLine($"No channel matching \"{query}\".");
            return 2;
        }

        // libvlc reports sout chain failures only through its own log, and a
        // recording that produces a zero-byte file says nothing otherwise.
        var engine = services.GetRequiredService<XtreamPlayer.Playback.LibVlcEngine>();

        if (engine.Options.Diagnostics)
        {
            engine.LibVlc.Log += (_, e) =>
            {
                // Warnings and errors only. A full debug dump is thousands of
                // lines and buries the one line that explains a failure — which
                // for a provider rejection is an HTTP status from the access
                // module, not anything the sout chain says.
                if (e.Level is LogLevel.Warning or LogLevel.Error)
                {
                    Console.WriteLine($"    [{e.Level}] {e.Module}: {e.Message}");
                }
            };
        }

        var recordings = services.GetRequiredService<XtreamPlayer.Playback.IRecordingService>();
        var paths = services.GetRequiredService<
            XtreamPlayer.Infrastructure.Downloads.IDownloadPaths>();

        var target = paths.ForRecordingAsync(channel.Name, null).GetAwaiter().GetResult();

        Console.WriteLine($"Recording: {channel.Name}");
        Console.WriteLine($"To       : {target}");

        var session = recordings.Start(channel.Id, channel.Name, channel.StreamUrl, target);

        for (var i = 0; i < seconds; i++)
        {
            Thread.Sleep(1000);
            Console.WriteLine($"  {i + 1}s  {session.Bytes / 1024.0:F0} KB");
        }

        recordings.StopAsync(session.Id).GetAwaiter().GetResult();

        var size = File.Exists(target) ? new FileInfo(target).Length : 0;

        Console.WriteLine();
        Console.WriteLine($"wrote    : {size / (1024.0 * 1024):F2} MB to {target}");

        // A .ts that never grew means the sout chain never received anything,
        // which is the failure worth catching here.
        return size > 0 ? 0 : 1;
    }

    private static XtreamPlayer.Core.Models.Profile? ActiveProfile(IServiceProvider services)
    {
        var settings = services
            .GetRequiredService<XtreamPlayer.Core.Abstractions.ISettingsRepository>();

        var profiles = services
            .GetRequiredService<XtreamPlayer.Core.Abstractions.IProfileRepository>();

        var activeId = settings
            .GetAsync(XtreamPlayer.Core.Abstractions.SettingKeys.ActiveProfileId)
            .GetAwaiter().GetResult();

        var all = profiles.ListAsync().GetAwaiter().GetResult();

        return all.FirstOrDefault(p => p.Id == activeId) ?? all.FirstOrDefault();
    }

    private static int RunGuideRefresh(IHost host)
    {
        var profiles = host.Services
            .GetRequiredService<XtreamPlayer.Core.Abstractions.IProfileRepository>();

        var settings = host.Services
            .GetRequiredService<XtreamPlayer.Core.Abstractions.ISettingsRepository>();

        var catalog = host.Services
            .GetRequiredService<XtreamPlayer.Infrastructure.Xtream.ICatalogStore>();

        var epg = host.Services
            .GetRequiredService<XtreamPlayer.Infrastructure.Epg.IEpgService>();

        var activeId = settings
            .GetAsync(XtreamPlayer.Core.Abstractions.SettingKeys.ActiveProfileId)
            .GetAwaiter().GetResult();

        var all = profiles.ListAsync().GetAwaiter().GetResult();
        var profile = all.FirstOrDefault(p => p.Id == activeId) ?? all.FirstOrDefault();

        if (profile is null)
        {
            Console.Error.WriteLine("No provider configured.");
            return 2;
        }

        epg.Progress += (_, progress) => Console.WriteLine("  " + progress.Message);

        // The guide is matched against the live catalog, so it has to be loaded.
        catalog.LoadAsync(profile).GetAwaiter().GetResult();

        var channels = catalog.Current.Live.Items;
        Console.WriteLine($"Matching against {channels.Count:N0} live channels.");

        epg.RefreshAsync(profile, channels).GetAwaiter().GetResult();

        var scope = new XtreamPlayer.Core.Models.ProfileScope(profile.Id);
        var state = epg.GetStateAsync(scope).GetAwaiter().GetResult();
        var mappings = epg.GetMappingsAsync(scope).GetAwaiter().GetResult();

        var matched = mappings.Values.Count(m => m.IsMatched);

        Console.WriteLine();
        Console.WriteLine($"channels   : {state.ChannelCount:N0}");
        Console.WriteLine($"programmes : {state.ProgrammeCount:N0}");
        // Both numbers, because only the first one means anything. A provider
        // publishes a guide for a fraction of its channels, so coverage of the
        // FEED is the measure of whether matching worked.
        var feedCovered = mappings.Values
            .Where(m => m.XmltvChannelId is not null)
            .Select(m => m.XmltvChannelId!)
            .Distinct(StringComparer.Ordinal)
            .Count();

        Console.WriteLine(
            $"feed cover : {feedCovered:N0} of {state.ChannelCount:N0} guide channels matched "
          + $"({feedCovered * 100.0 / Math.Max(1, state.ChannelCount):F1}%)");

        Console.WriteLine(
            $"streams    : {matched:N0} of {mappings.Count:N0} have listings "
          + $"({matched * 100.0 / Math.Max(1, mappings.Count):F1}%)");

        foreach (var group in mappings.Values.GroupBy(m => m.Method).OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"  {group.Key,-14} {group.Count():N0}");
        }

        if (state.Error is { } error)
        {
            Console.WriteLine($"error      : {error}");
        }

        // Prove the stored guide is actually queryable, not just ingested.
        var now = DateTimeOffset.Now;
        var guide = epg.GetGuideAsync(scope, now.AddHours(-1), now.AddHours(4))
            .GetAwaiter().GetResult();

        Console.WriteLine();
        Console.WriteLine($"channels with listings in the next 4h: {guide.Count:N0}");

        foreach (var (channelId, programmes) in guide.Take(5))
        {
            var channel = channels.FirstOrDefault(c => c.Id == channelId);
            var onNow = programmes.FirstOrDefault(p => p.IsNowPlaying(now));

            Console.WriteLine(
                $"  {channel?.Name ?? channelId,-38} {programmes.Count,3} listings"
              + (onNow is null ? string.Empty : $"  now: {onNow.Title}"));
        }

        return 0;
    }

    private static IHost BuildHost(bool verbose) =>
        Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((_, services) => services.AddXtreamPlayer(verbose))
            .Build();

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

internal static class Crash
{
    public static void Report(string headline, Exception? ex, string? remedy = null)
    {
        var text = $"""
            ====================================================================
            {headline}
            {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}
            {(remedy is null ? string.Empty : remedy + Environment.NewLine)}
            {ex}
            ====================================================================
            """;

        try
        {
            Log.Fatal(ex, "{Headline}", headline);
        }
        catch (Exception)
        {
            // Logging itself may be the thing that failed.
        }

        Console.Error.WriteLine(text);

        // The console can be gone by the time this runs. A file cannot.
        try
        {
            var path = Path.Combine(DatabasePaths.LogDirectory, "crash.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, text + Environment.NewLine);
            Console.Error.WriteLine($"Written to {path}");
        }
        catch (Exception writeFailure)
        {
            Console.Error.WriteLine($"(could not write crash log: {writeFailure.Message})");
        }
    }
}
