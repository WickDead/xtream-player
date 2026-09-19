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
