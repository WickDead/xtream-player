using System.Buffers.Binary;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace XtreamPlayer.Playback;

public sealed class VlcInitializationException(string message, string? remedy = null)
    : Exception(message)
{
    /// <summary>Platform-specific advice to show the user, e.g. "sudo apt install vlc".</summary>
    public string? Remedy { get; } = remedy;
}

/// <summary>
/// Locates and initializes native libvlc.
/// </summary>
/// <remarks>
/// The shipping app uses the bundled natives from VideoLAN.LibVLC.Windows /
/// .Mac, never the user's own VLC install: version skew against whatever VLC
/// the user happens to have is a support nightmare. The discovery chain below
/// is a fallback for when the bundled natives are missing (and the only path on
/// Linux, which has no NuGet runtime package).
/// </remarks>
public static class VlcLocator
{
    private static readonly object Gate = new();
    private static bool _initialized;

    /// <summary>Where libvlc was actually loaded from, once initialized.</summary>
    public static string? ResolvedDirectory { get; private set; }

    /// <summary>
    /// Idempotent. Must run before any LibVLC object is constructed, and on
    /// macOS before AppBuilder.Configure&lt;App&gt;().
    /// </summary>
    /// <remarks>
    /// LibVLCSharp's Core is fully qualified below: inside namespace
    /// XtreamPlayer.Playback the bare name Core binds to XtreamPlayer.Core,
    /// which wins over the using directive.
    /// </remarks>
    /// <param name="overridePath">
    /// The <c>vlc.path</c> setting, for debugging against a specific install.
    /// </param>
    public static void Initialize(string? overridePath = null)
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            foreach (var candidate in EnumerateCandidates(overridePath))
            {
                if (candidate is not null)
                {
                    ValidateArchitecture(candidate);
                }

                try
                {
                    LibVLCSharp.Shared.Core.Initialize(candidate);
                    ResolvedDirectory = candidate ?? "(bundled or system default)";
                    _initialized = true;
                    return;
                }
                catch (Exception ex) when (ex is VLCException or DllNotFoundException)
                {
                    // Try the next candidate.
                }
            }

            throw new VlcInitializationException(
                "Could not load libvlc. The video engine is unavailable.",
                RemedyForPlatform());
        }
    }

    /// <summary>
    /// null means "let LibVLCSharp use the bundled natives, or the system
    /// loader on Linux" and is always tried first.
    /// </summary>
    private static IEnumerable<string?> EnumerateCandidates(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && Directory.Exists(overridePath))
        {
            yield return overridePath;
        }

        yield return null;

        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        // Fallback only. Note there is deliberately no registry lookup here:
        // reading HKLM would force a net10.0-windows TFM on a cross-platform
        // project for one fallback path. A custom install directory is covered
        // by the vlc.path setting instead.
        foreach (var root in new[]
                 {
                     Environment.GetEnvironmentVariable("ProgramFiles"),
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                     Environment.GetEnvironmentVariable("ProgramW6432"),
                 })
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var dir = Path.Combine(root, "VideoLAN", "VLC");
            if (File.Exists(Path.Combine(dir, "libvlc.dll")))
            {
                yield return dir;
            }
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = dir.Trim();
            if (trimmed.Length > 0 && File.Exists(Path.Combine(trimmed, "libvlc.dll")))
            {
                yield return trimmed;
            }
        }
    }

    private static string RemedyForPlatform()
    {
        if (OperatingSystem.IsLinux())
        {
            return "Install VLC from your distribution: "
                 + "Debian/Ubuntu `sudo apt install vlc`, "
                 + "Fedora (rpmfusion) `sudo dnf install vlc`. "
                 + "The vlc-plugin-base package is required as well.";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "Install VLC from https://www.videolan.org/vlc/ , "
                 + "or set the vlc.path setting to a directory containing libvlc.dylib.";
        }

        return "Reinstall Xtream Player, or set the vlc.path setting to a "
             + "directory containing libvlc.dll.";
    }

    // ---- PE architecture validation -------------------------------------
    // Ported from the previous app's src-tauri/src/api/vlc.rs:729-767.
    //
    // This is dead weight against the bundled natives (a fixed win-x64 RID makes
    // a mismatch impossible), but "user has 32-bit VLC installed, app is x64" is
    // a genuine scenario on the fallback discovery path, and it otherwise
    // surfaces as an inscrutable DllNotFoundException.

    private enum BinaryArchitecture
    {
        X86,
        X64,
        Arm,
        Arm64,
        Unknown,
    }

    private static void ValidateArchitecture(string installRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dllPath = Path.Combine(installRoot, "libvlc.dll");
        if (!File.Exists(dllPath))
        {
            return;
        }

        var dllArch = ReadPeMachineArchitecture(dllPath);
        if (dllArch is null)
        {
            throw new VlcInitializationException(
                $"Unable to inspect {dllPath} to determine whether it matches this app build.");
        }

        var processArch = CurrentProcessArchitecture();
        if (dllArch == processArch)
        {
            return;
        }

        throw new VlcInitializationException(
            $"Installed VLC at {installRoot} is {Label(dllArch.Value)}, but this app is "
          + $"{Label(processArch)}. Install a {Label(processArch)} VLC build or run a "
          + "matching app build.");
    }

    private static BinaryArchitecture CurrentProcessArchitecture() =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => BinaryArchitecture.X86,
            Architecture.X64 => BinaryArchitecture.X64,
            Architecture.Arm => BinaryArchitecture.Arm,
            Architecture.Arm64 => BinaryArchitecture.Arm64,
            _ => BinaryArchitecture.Unknown,
        };

    private static string Label(BinaryArchitecture arch) => arch switch
    {
        BinaryArchitecture.X86 => "32-bit (x86)",
        BinaryArchitecture.X64 => "64-bit (x64)",
        BinaryArchitecture.Arm => "ARM",
        BinaryArchitecture.Arm64 => "ARM64",
        _ => "an unrecognized architecture",
    };

    /// <summary>
    /// Reads the PE COFF machine field. Unlike the Rust original this reads only
    /// the handful of bytes it needs rather than slurping the whole ~2 MB DLL.
    /// </summary>
    private static BinaryArchitecture? ReadPeMachineArchitecture(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> buffer = stackalloc byte[4];

            // DOS header: "MZ" magic, then e_lfanew at 0x3C.
            if (!ReadExactlyAt(stream, 0, buffer[..2])
                || buffer[0] != (byte)'M'
                || buffer[1] != (byte)'Z')
            {
                return null;
            }

            if (!ReadExactlyAt(stream, 0x3C, buffer))
            {
                return null;
            }

            var peOffset = BinaryPrimitives.ReadUInt32LittleEndian(buffer);

            // PE signature "PE\0\0", then the 2-byte machine field.
            if (!ReadExactlyAt(stream, peOffset, buffer)
                || buffer[0] != (byte)'P'
                || buffer[1] != (byte)'E'
                || buffer[2] != 0
                || buffer[3] != 0)
            {
                return null;
            }

            if (!ReadExactlyAt(stream, peOffset + 4, buffer[..2]))
            {
                return null;
            }

            return BinaryPrimitives.ReadUInt16LittleEndian(buffer[..2]) switch
            {
                0x014c => BinaryArchitecture.X86,
                0x8664 => BinaryArchitecture.X64,
                0x01c4 => BinaryArchitecture.Arm,
                0xAA64 => BinaryArchitecture.Arm64,
                _ => BinaryArchitecture.Unknown,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool ReadExactlyAt(FileStream stream, long offset, Span<byte> destination)
    {
        if (offset < 0 || offset + destination.Length > stream.Length)
        {
            return false;
        }

        stream.Seek(offset, SeekOrigin.Begin);
        return stream.ReadAtLeast(destination, destination.Length, throwOnEndOfStream: false)
               == destination.Length;
    }
}
