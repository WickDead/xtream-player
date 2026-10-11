using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using XtreamPlayer.App.ViewModels;
using XtreamPlayer.App.Views;

namespace XtreamPlayer.App;

public partial class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = Program.Host?.Services
                ?? throw new InvalidOperationException("The host was not built before startup.");

            // Resolved here, on the UI thread, deliberately. VlcPlaybackService
            // captures SynchronizationContext.Current at construction so its
            // events arrive already marshalled; resolving it from a background
            // thread would silently lose that.
            // The poster cache is handed to AsyncImage statically: the control
            // is created thousands of times in a grid and must not resolve a
            // service on every construction.
            Media.AsyncImage.Cache = services.GetRequiredService<Media.IPosterCache>();

            var shell = services.GetRequiredService<ShellViewModel>();

            var window = new MainWindow { DataContext = shell };

            // Time to a window the user can actually act on, measured from
            // process start rather than from here — everything before this point
            // (runtime init, libvlc load, migrations, DI) is part of the wait.
            window.Opened += (_, _) =>
            {
                var elapsed = DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime;

                Serilog.Log.Information(
                    "Window shown {Milliseconds:F0} ms after process start.",
                    elapsed.TotalMilliseconds);
            };

            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
