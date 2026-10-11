using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace XtreamPlayer.Server;

public sealed class MediaTickets(IDataProtectionProvider protection, ServerStore store, ServerOptions options)
{
    private readonly IDataProtector _protector = protection.CreateProtector("XtreamPlayer.Media.v1");
    public sealed record Grant(string Device, string Scope, string Resource, DateTimeOffset Expires);
    public string Issue(string device, string scope, string resource, TimeSpan? ttl = null) =>
        _protector.Protect(JsonSerializer.Serialize(new Grant(device, scope, resource, DateTimeOffset.UtcNow + (ttl ?? TimeSpan.FromHours(12)))));
    public Grant Read(string ticket, string scope)
    {
        Grant? grant;
        try { grant = JsonSerializer.Deserialize<Grant>(_protector.Unprotect(ticket)); }
        catch { throw new UnauthorizedAccessException(); }
        if (grant is null || grant.Scope != scope || grant.Expires < DateTimeOffset.UtcNow ||
            store.Get<ServerStore.DeviceRecord>("device", grant.Device) is null) throw new UnauthorizedAccessException();
        return grant;
    }
    private static string RelayExtension(string url)
    {
        var extension = Path.GetExtension(new Uri(url).AbsolutePath);
        return System.Text.RegularExpressions.Regex.IsMatch(extension, "^\\.[a-zA-Z0-9]{1,8}$") ? extension : ".bin";
    }
    public string Public(string path) => options.PublicUrl.TrimEnd('/') + path;
    public string Relay(string device, string url) => options.InternalUrl.TrimEnd('/') + "/media/relay/source" + RelayExtension(url) + "?t=" + Uri.EscapeDataString(Issue(device, "relay", url));
}
