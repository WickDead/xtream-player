using Avalonia;
using XtreamPlayer.Playback;

namespace VideoSpike;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // A GUI app that dies during startup otherwise prints nothing anywhere
        // and simply looks like it did not run. Everything below exists so that
        // never happens again.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Fail("Unhandled exception", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
            Fail("Unobserved task exception", e.Exception);

        // Must happen before AppBuilder on macOS, and is harmless to front-load
        // everywhere else.
        try
        {
            VlcLocator.Initialize();
            Console.WriteLine($"libvlc resolved from: {VlcLocator.ResolvedDirectory}");
        }
        catch (VlcInitializationException ex)
        {
            Fail("libvlc could not be loaded", ex, ex.Remedy);
            return 2;
        }

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Fail("The app failed to start", ex);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void Fail(string headline, Exception? ex, string? remedy = null)
    {
        var text = $"""
            ====================================================================
            {headline}
            {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}
            {(remedy is null ? string.Empty : remedy + Environment.NewLine)}
            {ex}
            ====================================================================
            """;

        Console.Error.WriteLine(text);

        // The console can be gone by the time this runs. The file cannot.
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "videospike-crash.log");
            File.AppendAllText(path, text + Environment.NewLine);
            Console.Error.WriteLine($"Written to {path}");
        }
        catch (Exception writeFailure)
        {
            Console.Error.WriteLine($"(could not write crash log: {writeFailure.Message})");
        }
    }
}
