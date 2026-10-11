# Third-party software

The installer ships these alongside the application. Versions and licence
identifiers are taken from the packages actually restored, not from memory.

## LGPL-2.1-or-later

These matter most, because the LGPL carries obligations on anyone redistributing
them — which this installer does.

| | Version | |
| --- | --- | --- |
| [libvlc](https://www.videolan.org/vlc/libvlc.html) (`VideoLAN.LibVLC.Windows`) | 3.0.23.1 | LGPL-2.1-or-later |
| [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp) | 3.10.1 | LGPL-2.1-or-later |

libvlc ships as its own DLLs beside the executable, with its plugin tree intact,
rather than being linked into it. Anyone who wants to run this against a
different build of libvlc can replace those files — which is what the LGPL asks
for, and incidentally the reason the app does not use whatever VLC you happen to
have installed.

Source for both is available from VideoLAN at the links above. Neither is
modified here.

## MIT

| | Version |
| --- | --- |
| [Avalonia](https://avaloniaui.net) | 11.3.22 |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.2 |
| [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/) | 10.0.12 |
| [SkiaSharp](https://github.com/mono/SkiaSharp) | 3.119.0 |

## Apache-2.0

| | Version |
| --- | --- |
| [Dapper](https://github.com/DapperLib/Dapper) | 2.1.86 |
| [Serilog](https://serilog.net) | 4.4.0 |
| SQLitePCLRaw | bundled with Microsoft.Data.Sqlite |

## Public domain

SQLite itself, via SQLitePCLRaw.

## Not bundled

The app talks to an Xtream Codes provider over HTTP. It ships no provider
credentials, no channel lists and no media, and it is not affiliated with any
provider. What it does with your account is what you tell it to.

## Web application and backend

The PWA uses ASP.NET Core / Blazor WebAssembly (.NET Foundation, MIT).
The optional Docker backend installs FFmpeg from its base distribution, with
H.264 support through libx264. FFmpeg licensing depends on the distribution's
build configuration (GPL when built with libx264); these remain separate server
executables. Preserve the distribution package's license/source notices when
redistributing a built image. See https://ffmpeg.org/legal.html and the source
packages for the chosen base image. No FFmpeg binaries are committed to this repo.

The PWA bundles hls.js 1.7.3 (Apache-2.0), from the official hls.js npm package. Its license is included at src/XtreamPlayer.Web/wwwroot/vendor/hls.LICENSE. See https://github.com/video-dev/hls.js.
