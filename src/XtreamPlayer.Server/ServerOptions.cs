namespace XtreamPlayer.Server;

public sealed class ServerOptions
{
    public string DataDirectory { get; set; } = "data";
    public string PairingCode { get; set; } = "";
    public string InternalUrl { get; set; } = "http://127.0.0.1:5080";
    public string PublicUrl { get; set; } = "http://localhost:5080";
    public string[] AllowedOrigins { get; set; } = ["http://localhost:5081"];
    public string[] AllowedUpstreamHosts { get; set; } = [];
    public string Ffmpeg { get; set; } = "ffmpeg";
    public string Ffprobe { get; set; } = "ffprobe";
    public long MaxMediaBytes { get; set; } = 100L * 1024 * 1024 * 1024;
    public long MinimumFreeBytes { get; set; } = 512L * 1024 * 1024;
    public int MaxTranscodes { get; set; } = 1;
    public string MediaDirectory => Path.Combine(DataDirectory, "media");
}
