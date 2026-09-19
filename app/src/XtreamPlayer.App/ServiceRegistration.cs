using Microsoft.Extensions.DependencyInjection;
using XtreamPlayer.App.ViewModels;
using XtreamPlayer.Core.Abstractions;
using XtreamPlayer.Infrastructure.Migration;
using XtreamPlayer.Infrastructure.Persistence;
using XtreamPlayer.Infrastructure.Security;
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

        // ---- playback ----
        // The engine owns one LibVLC and one long-lived MediaPlayer, so both are
        // singletons. Creating a MediaPlayer per stream churns the native video
        // surface and causes intermittent black frames and lost audio.
        services.AddSingleton(_ => new LibVlcOptions { Diagnostics = verbose });
        services.AddSingleton<LibVlcEngine>();
        services.AddSingleton<PlaybackPreferences>();
        services.AddSingleton<IPlaybackService, VlcPlaybackService>();

        // ---- view models ----
        services.AddSingleton<ShellViewModel>();

        return services;
    }
}
