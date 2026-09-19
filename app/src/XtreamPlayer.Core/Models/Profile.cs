namespace XtreamPlayer.Core.Models;

/// <summary>How a profile's password is protected at rest.</summary>
/// <remarks>
/// Stored per row, not globally. A database copied to another machine cannot be
/// DPAPI-decrypted, and that must surface as "re-enter your password for this
/// provider" rather than a crash or silent garbage credentials.
/// </remarks>
public enum PasswordProtection
{
    /// <summary>UTF-8 bytes, no protection. The fallback everywhere it is unavailable.</summary>
    Plaintext,

    /// <summary>Windows DPAPI, scoped to the current user.</summary>
    DpapiUser,

    /// <summary>Linux libsecret via secret-tool.</summary>
    LibSecret,

    /// <summary>macOS Keychain.</summary>
    Keychain,
}

/// <summary>
/// Which container the live catalog should request.
/// </summary>
public enum LiveStreamFormat
{
    /// <summary>
    /// Prefer <c>ts</c>, else <c>m3u8</c>, following the provider's
    /// allowed_output_formats. This is the right default: libvlc decodes raw
    /// MPEG-TS natively, which is precisely what the old webview player could not.
    /// </summary>
    Auto,

    Ts,
    Hls,
}

/// <summary>A saved Xtream provider.</summary>
public sealed record Profile
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Normalized: scheme prepended if missing, trailing slashes trimmed.</summary>
    public required string ServerUrl { get; init; }

    public required string Username { get; init; }

    /// <summary>Never persisted in the clear unless <see cref="Protection"/> says so.</summary>
    public required string Password { get; init; }

    public PasswordProtection Protection { get; init; } = PasswordProtection.Plaintext;

    /// <summary>
    /// True when the stored password could not be decrypted — typically the
    /// database was copied from another machine or user account.
    /// </summary>
    public bool PasswordUnavailable { get; init; }

    public string? Status { get; init; }

    /// <summary>Raw provider value: unix seconds as a string, or null for unlimited.</summary>
    public string? ExpiresAt { get; init; }

    public int? MaxConnections { get; init; }

    public LiveStreamFormat PreferredLiveFormat { get; init; } = LiveStreamFormat.Auto;

    public int SortIndex { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? ExpiryDate =>
        long.TryParse(ExpiresAt, out var seconds) && seconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
}
