using Avalonia;
using XtreamPlayer.Playback;

namespace VideoSpike;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Must happen before AppBuilder on macOS, and is harmless to front-load
        // everywhere else. A failure here is the one error worth showing before
        // the UI exists at all.
        try
        {
            VlcLocator.Initialize();
            Console.WriteLine($"libvlc resolved from: {VlcLocator.ResolvedDirectory}");
        }
        catch (VlcInitializationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(ex.Remedy);
            return 2;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
