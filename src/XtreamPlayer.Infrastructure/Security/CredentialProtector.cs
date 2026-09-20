using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using XtreamPlayer.Core.Models;

namespace XtreamPlayer.Infrastructure.Security;

public interface ICredentialProtector
{
    PasswordProtection Scheme { get; }

    byte[] Protect(string plaintext);

    /// <summary>
    /// Returns null when the value cannot be decrypted — typically a database
    /// copied from another machine or user account. Callers surface that as
    /// "re-enter your password", never as a crash.
    /// </summary>
    string? Unprotect(byte[] ciphertext, PasswordProtection scheme);
}

/// <summary>
/// Encrypts provider passwords at rest.
/// </summary>
/// <remarks>
/// <para>
/// Be clear about the ceiling: the app must decrypt unattended in order to build
/// stream URLs, so this is obfuscation plus OS access control, not a secret
/// store. Anything running as this user can recover the password.
/// </para>
/// <para>
/// More importantly, the password is embedded in <em>every stream URL</em>
/// (<c>/live/{user}/{pass}/{id}.ts</c>), so it will otherwise appear verbatim in
/// logs, libvlc verbose output and error messages. The redacting log formatter
/// matters more in practice than this does — see
/// <c>CredentialRedactor</c>.
/// </para>
/// </remarks>
public sealed class CredentialProtector : ICredentialProtector
{
    // Mixed into DPAPI so a blob lifted from this database cannot be decrypted
    // by another application running as the same user without also knowing it.
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("XtreamPlayer.ProviderPassword.v1");

    public CredentialProtector(PasswordProtection? forceScheme = null)
    {
        Scheme = forceScheme ?? DetectScheme();
    }

    public PasswordProtection Scheme { get; }

    private static PasswordProtection DetectScheme()
    {
        if (OperatingSystem.IsWindows())
        {
            return PasswordProtection.DpapiUser;
        }

        // libsecret is only usable with a running session keyring. Probing for
        // the binary is cheap; failing at save time is not.
        if (OperatingSystem.IsLinux() && HasSecretTool())
        {
            return PasswordProtection.LibSecret;
        }

        return PasswordProtection.Plaintext;
    }

    private static bool HasSecretTool()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("secret-tool", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (process is null)
            {
                return false;
            }

            return process.WaitForExit(2000) && process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    public byte[] Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var bytes = Encoding.UTF8.GetBytes(plaintext);

        if (Scheme != PasswordProtection.DpapiUser || !OperatingSystem.IsWindows())
        {
            return bytes;
        }

        try
        {
            return ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            // Better a readable password than an unusable profile.
            return bytes;
        }
    }

    public string? Unprotect(byte[] ciphertext, PasswordProtection scheme)
    {
        if (ciphertext.Length == 0)
        {
            return string.Empty;
        }

        if (scheme != PasswordProtection.DpapiUser || !OperatingSystem.IsWindows())
        {
            return Encoding.UTF8.GetString(ciphertext);
        }

        try
        {
            var bytes = ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException)
        {
            // Wrong machine or wrong user. The caller marks the profile
            // PasswordUnavailable and asks for it again.
            return null;
        }
    }
}

public static class PasswordProtectionExtensions
{
    public static string ToStorageString(this PasswordProtection value) => value switch
    {
        PasswordProtection.DpapiUser => "dpapi-user",
        PasswordProtection.LibSecret => "libsecret",
        PasswordProtection.Keychain => "keychain",
        _ => "plaintext",
    };

    public static PasswordProtection ParseStorageString(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "dpapi-user" => PasswordProtection.DpapiUser,
            "libsecret" => PasswordProtection.LibSecret,
            "keychain" => PasswordProtection.Keychain,
            _ => PasswordProtection.Plaintext,
        };
}
