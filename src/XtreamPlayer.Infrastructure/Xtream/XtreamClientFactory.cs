using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using XtreamPlayer.Core.Models;

namespace XtreamPlayer.Infrastructure.Xtream;

public interface IXtreamClientFactory
{
    XtreamClient For(Profile profile);

    /// <summary>Drops the cached client after a profile's credentials change.</summary>
    void Invalidate(string profileId);
}

/// <summary>
/// One <see cref="XtreamClient"/> per profile, cached.
/// </summary>
/// <remarks>
/// The old Rust client built a new client and re-read the credentials out of
/// SQLite on every single call. Caching removes that per-call cost and, more
/// importantly, lets the authentication response (allowed formats, expiry,
/// connection limit) be reused instead of re-fetched.
/// </remarks>
public sealed class XtreamClientFactory(IHttpClientFactory httpFactory) : IXtreamClientFactory
{
    public const string HttpClientName = "xtream";

    private readonly ConcurrentDictionary<string, XtreamClient> _clients = new(StringComparer.Ordinal);

    public XtreamClient For(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return _clients.AddOrUpdate(
            profile.Id,
            _ => new XtreamClient(httpFactory.CreateClient(HttpClientName), profile),
            (_, existing) =>
                // Credentials or the preferred format may have changed since the
                // client was built; rebuild rather than serve a stale one.
                existing.Profile.ServerUrl == profile.ServerUrl
                && existing.Profile.Username == profile.Username
                && existing.Profile.Password == profile.Password
                && existing.Profile.PreferredLiveFormat == profile.PreferredLiveFormat
                    ? existing
                    : new XtreamClient(httpFactory.CreateClient(HttpClientName), profile));
    }

    public void Invalidate(string profileId) => _clients.TryRemove(profileId, out _);
}

public static class XtreamHttpRegistration
{
    /// <summary>
    /// Matches the user agent libvlc is given.
    /// </summary>
    /// <remarks>
    /// Some providers gate on user agent. If the catalog client and libvlc send
    /// different ones, catalogs load fine and every stream 403s — a failure that
    /// looks like a codec problem and is not.
    /// </remarks>
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) XtreamPlayer/1.0";

    public static IServiceCollection AddXtreamHttp(this IServiceCollection services)
    {
        services.AddHttpClient(XtreamClientFactory.HttpClientName, client =>
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

                // Per-request cancellation is used instead, because a 26 MB
                // catalog and a short auth call need very different limits.
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 6,
                UseCookies = false,

                // Providers 302 to a different host, sometimes twice, before
                // landing on a tokenised edge URL. Following redirects is not
                // optional here.
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5,
            });

        services.AddSingleton<IXtreamClientFactory, XtreamClientFactory>();

        return services;
    }
}
