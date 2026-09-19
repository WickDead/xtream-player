using Avalonia;
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
