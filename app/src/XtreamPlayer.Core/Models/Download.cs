namespace XtreamPlayer.Core.Models;

public enum DownloadState
{
    Queued,
    Downloading,
    Paused,
    Completed,
    Failed,

    /// <summary>Cancelled by the user. The partial file is kept so it can resume.</summary>
    Cancelled,
}

public sealed record DownloadJob
{
    public required string Id { get; init; }
    public required string ProfileId { get; init; }
    public required ContentKind Kind { get; init; }
    public required string ContentId { get; init; }
    public required string Title { get; init; }
    public required string SourceUrl { get; init; }
    public required string TargetPath { get; init; }

    public long TotalBytes { get; init; }
    public long DownloadedBytes { get; init; }

    /// <summary>
    /// Cleared once a server is seen answering a range request with 200 instead
    /// of 206, so we stop asking for a range it will never honour.
    /// </summary>
    public bool SupportsRange { get; init; } = true;

    public DownloadState State { get; init; } = DownloadState.Queued;
    public string? Error { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public double Progress => TotalBytes > 0
        ? Math.Clamp((double)DownloadedBytes / TotalBytes, 0, 1)
        : 0;

    public bool IsActive => State is DownloadState.Queued or DownloadState.Downloading;

    /// <summary>The file being written. Renamed on completion.</summary>
    public string PartialPath => TargetPath + ".part";
}
