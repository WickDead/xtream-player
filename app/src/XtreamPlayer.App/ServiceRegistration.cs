using Microsoft.Extensions.DependencyInjection;
using XtreamPlayer.App.Media;
using XtreamPlayer.App.ViewModels;
using XtreamPlayer.Core.Abstractions;
using XtreamPlayer.Core.Library;
using XtreamPlayer.Core.Navigation;
using XtreamPlayer.Infrastructure.Migration;
using XtreamPlayer.Infrastructure.Persistence;
using XtreamPlayer.Infrastructure.Security;
using XtreamPlayer.Infrastructure.Xtream;
using XtreamPlayer.Playback;

namespace XtreamPlayer.App;

public static class ServiceRegistration
{
    public static IServiceCollection AddXtreamPlayer(this IServiceCollection services, bool verbose = false)
    {
        ArgumentNullException.ThrowIfNull(services);

        // ---- persistence ----
        // One factory, so migrations run exactly once per process.
        services.AddSingleton<IDbConnectionFactory>(_ => new SqliteConnectionFactory());
        services.AddSingleton<ICredentialProtector>(_ => new CredentialProtector());

        services.AddSingleton<IProfileRepository, ProfileRepository>();
        services.AddSingleton<IFavoritesRepository, FavoritesRepository>();
        services.AddSingleton<IResumeRepository, ResumeRepository>();
        services.AddSingleton<IHiddenCategoryRepository, HiddenCategoryRepository>();
        services.AddSingleton<ISettingsRepository, SettingsRepository>();
        services.AddSingleton<LegacyImporter>();

        // ---- provider ----
        services.AddXtreamHttp();
        services.AddSingleton<ICatalogStore, CatalogStore>();
        services.AddSingleton<IFavoritesStore, FavoritesStore>();

        // Posters get their own client: a separate connection pool keeps a burst
        // of image fetches from competing with a catalog download.
        services.AddHttpClient("posters", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(XtreamHttpRegistration.UserAgent);
        });

        services.AddSingleton<IPosterCache, PosterCache>();

        // ---- playback ----
        // The engine owns one LibVLC and one long-lived MediaPlayer, so both are
        // singletons: creating a MediaPlayer per stream churns the native video
        // surface and causes intermittent black frames and lost audio.
        services.AddSingleton(_ => new LibVlcOptions { Diagnostics = verbose });
        services.AddSingleton<LibVlcEngine>();
        services.AddSingleton<PlaybackPreferences>();
        services.AddSingleton<IPlaybackService, VlcPlaybackService>();

        // ---- navigation ----
        // Pages are transient: the navigation stack owns their lifetime, and a
        // singleton page could not appear twice in a back stack.
        services.AddTransient<DashboardPageViewModel>();
        services.AddTransient<ProvidersPageViewModel>();
        services.AddTransient<LivePageViewModel>();
        services.AddTransient<VodPageViewModel>();
        services.AddTransient<VodDetailPageViewModel>();
        services.AddTransient<SeriesPageViewModel>();
        services.AddTransient<SeriesDetailPageViewModel>();
        services.AddTransient<PlayerPageViewModel>();
        services.AddTransient<SettingsPageViewModel>();

        services.AddSingleton<INavigationService>(provider =>
            new NavigationService(route => CreatePage(provider, route)));

        services.AddSingleton<ShellViewModel>();

        return services;
    }

    /// <summary>
    /// Builds the page for a route.
    /// </summary>
    /// <remarks>
    /// An explicit switch rather than convention-based reflection: nine pages,
    /// and a missing case is visible here rather than as a runtime surprise.
    /// </remarks>
    private static PageViewModelBase CreatePage(IServiceProvider provider, NavRoute route) => route.Key switch
    {
        PageKey.Dashboard => provider.GetRequiredService<DashboardPageViewModel>(),
        PageKey.Providers => provider.GetRequiredService<ProvidersPageViewModel>(),
        PageKey.Live => provider.GetRequiredService<LivePageViewModel>(),
        PageKey.Vod => provider.GetRequiredService<VodPageViewModel>(),
        PageKey.VodDetail => provider.GetRequiredService<VodDetailPageViewModel>(),
        PageKey.Series => provider.GetRequiredService<SeriesPageViewModel>(),
        PageKey.SeriesDetail => provider.GetRequiredService<SeriesDetailPageViewModel>(),
        PageKey.Player => provider.GetRequiredService<PlayerPageViewModel>(),
        PageKey.Settings => provider.GetRequiredService<SettingsPageViewModel>(),

        // Guide and Downloads land in later phases; until then they are the
        // dashboard rather than a crash.
        _ => provider.GetRequiredService<DashboardPageViewModel>(),
    };
}
