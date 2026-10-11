using System.Text.RegularExpressions;
using Microsoft.Net.Http.Headers;

namespace XtreamPlayer.Server;

public static partial class Relay
{
    [GeneratedRegex("URI=\"([^\"]+)\"")]
    private static partial Regex UriAttribute();

    public static string Rewrite(string manifest, Uri source, Func<string, string> link)
    {
        if (manifest.Contains("{$", StringComparison.Ordinal)) throw new InvalidOperationException("HLS variable substitution is not supported.");
        if (!manifest.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal)) throw new InvalidOperationException("Invalid HLS playlist.");
        return string.Join("\n", manifest.Split('\n').Select(line =>
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) return line;
            if (!trimmed.StartsWith('#')) return link(new Uri(source, trimmed).AbsoluteUri);
            return UriAttribute().Replace(line, m => "URI=\"" + link(new Uri(source, m.Groups[1].Value).AbsoluteUri) + "\"");
        }));
    }
    public static async Task Serve(HttpContext context, string ticket, MediaTickets tickets, IHttpClientFactory clients)
    {
        var grant = tickets.Read(ticket, "relay");
        using var request = new HttpRequestMessage(HttpMethod.Get, grant.Resource);
        if (context.Request.Headers.Range.Count > 0)
            request.Headers.Range = System.Net.Http.Headers.RangeHeaderValue.Parse(context.Request.Headers.Range.ToString());
        using var response = await clients.CreateClient("upstream").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.Headers.CacheControl = "no-store";
        if (!response.IsSuccessStatusCode) return;
        var type = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        var finalUri = response.RequestMessage?.RequestUri ?? new Uri(grant.Resource);
        await using var input = await response.Content.ReadAsStreamAsync(context.RequestAborted);
        var prefix = new byte[1024];
        var prefixLength = await input.ReadAtLeastAsync(prefix, (int)Math.Min(16, response.Content.Headers.ContentLength ?? 16), false, context.RequestAborted);
        var beginning = System.Text.Encoding.UTF8.GetString(prefix, 0, prefixLength).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        if (beginning.StartsWith("#EXTM3U", StringComparison.Ordinal) || finalUri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || type.Contains("mpegurl", StringComparison.OrdinalIgnoreCase))
        {
            using var buffer = new MemoryStream();
            buffer.Write(prefix, 0, prefixLength);
            var chunk = new byte[65536]; int count;
            while ((count = await input.ReadAsync(chunk, context.RequestAborted)) > 0)
            {
                if (buffer.Length + count > 2 * 1024 * 1024) throw new InvalidOperationException("Playlist too large.");
                buffer.Write(chunk, 0, count);
            }
            var manifest = Rewrite(System.Text.Encoding.UTF8.GetString(buffer.ToArray()).TrimStart('\uFEFF'), finalUri, url => tickets.Relay(grant.Device, url));
            context.Response.ContentType = "application/vnd.apple.mpegurl";
            await context.Response.WriteAsync(manifest, context.RequestAborted);
            return;
        }
        context.Response.ContentType = type;
        if (response.Content.Headers.ContentLength is { } length) context.Response.ContentLength = length;
        if (response.Content.Headers.ContentRange is { } range) context.Response.Headers.ContentRange = range.ToString();
        context.Response.Headers.AcceptRanges = "bytes";
        await context.Response.Body.WriteAsync(prefix.AsMemory(0, prefixLength), context.RequestAborted);
        await input.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
}
