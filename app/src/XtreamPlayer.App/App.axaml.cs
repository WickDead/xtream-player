using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using XtreamPlayer.App.ViewModels;
using XtreamPlayer.App.Views;

namespace XtreamPlayer.App;

public partial class App : Application
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

            desktop.MainWindow = new MainWindow { DataContext = shell };
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
