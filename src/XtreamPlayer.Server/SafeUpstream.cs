using System.Net;
using System.Net.Sockets;
using XtreamPlayer.Core.Models;

namespace XtreamPlayer.Server;

/// <summary>Pin connections to validated public IPs; recheck every redirect and never forward user headers.</summary>
public sealed class SafeUpstream(ServerOptions options, ServerStore store) : DelegatingHandler
{
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var b = ip.GetAddressBytes();
        if (IPAddress.IsLoopback(ip)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return b[0] is not (0 or 10 or 127) && b[0] < 224 &&
                !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] >= 16 && b[1] <= 31) &&
                !(b[0] == 192 && b[1] == 168) && !(b[0] == 100 && b[1] >= 64 && b[1] <= 127) &&
                !(b[0] == 198 && b[1] is 18 or 19);
        return ip.AddressFamily == AddressFamily.InterNetworkV6 && (b[0] & 0xe0) == 0x20;
    }
    public void Validate(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Only HTTP(S) provider addresses are supported.");
        var known = store.List<Profile>("profile").Select(p => new Uri(p.ServerUrl).IdnHost);
        if (!known.Concat(options.AllowedUpstreamHosts).Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Upstream host '{uri.IdnHost}' is not approved. Add its hostname to AllowedUpstreamHosts on the server.");
        if (IPAddress.TryParse(uri.Host, out var ip) && !IsPublic(ip))
            throw new InvalidOperationException("Private upstream addresses are not allowed.");
    }
    public static SocketsHttpHandler Transport() => new()
    {
        AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            if (addresses.Length == 0 || addresses.Any(a => !IsPublic(a)))
                throw new HttpRequestException("Upstream must resolve exclusively to public addresses.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct); return new NetworkStream(socket, true); }
            catch { socket.Dispose(); throw; }
        }
    };
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        for (var i = 0; i <= 5; i++)
        {
            Validate(uri);
            using var next = new HttpRequestMessage(HttpMethod.Get, uri);
            next.Headers.UserAgent.ParseAdd("XtreamPlayer/1.0");
            next.Headers.Range = request.Headers.Range;
            next.Headers.IfRange = request.Headers.IfRange;
            var response = await base.SendAsync(next, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            { uri = new Uri(uri, location); response.Dispose(); continue; }
            return response;
        }
        throw new HttpRequestException("Too many provider redirects.");
    }
}
