# Xtream Player

A Windows-first desktop client for Xtream IPTV providers, built on Avalonia and
LibVLC. Cross-platform by construction; only Windows is verified.

Three things drove the rewrite:

- **Navigation that remembers where you were.** Going back returns you to the
  exact category, filter text, selection and scroll offset you left — including
  after closing the player.
- **One video engine that handles everything.** Live MPEG-TS, HLS, VOD and local
  files through the same player, with audio and subtitle track switching.
- **A real XMLTV guide**, not a placeholder.

## Status

| Phase | |
| --- | --- |
| 0 — Embedded video proof | Done |
| 1 — Persistence and migration | Done |
| 2 — Xtream client and catalog | Done |
| 3 — Navigation and shell | Done |
| 4 — Browse UI | Done |
| 5 — Player and resume | Done |
| 6 — XMLTV guide | Done |
| 7 — Downloads and recording | Done |
| 8 — Packaging | Done |

Verified against a live provider: 11,967 live channels, 68,595 movies, 7,146
series, and an XMLTV feed of ~21,700 programmes across 1,082 channels.

> **The previous Tauri/React app still sits at the repository root** and remains
> functional. It is the fallback and the reference for provider response shapes,
> and it comes out only after a stretch of daily use on the new app. Its final
> state is tagged `v0.1.2-tauri-final`.

## Building

Requires the .NET 10 SDK. Everything else, including libvlc, comes from NuGet.

```bash
dotnet build app/XtreamPlayer.slnx
```

```bash
dotnet test app/XtreamPlayer.slnx
```

```bash
dotnet run --project app/src/XtreamPlayer.App/XtreamPlayer.App.csproj
```

### Releasing

Publishes a self-contained `win-x64` payload to `dist/XtreamPlayer`, smoke-tests
it, and builds an installer if Inno Setup is on PATH:

```bash
pwsh app/packaging/build-release.ps1
```

No .NET runtime is needed on the target machine, and libvlc ships with the app
rather than being borrowed from whatever VLC the user happens to have installed —
version skew against somebody's own VLC is a support problem with no good answer.

### The icon

The app icon is geometry, generated rather than drawn once and lost:

```bash
dotnet run app/packaging/make-icon.cs
```

It writes a nine-size `.ico` plus PNG renders to
`app/src/XtreamPlayer.App/Assets`. Below 48px the seek bar is dropped and the
play triangle grows to fill the tile, because at 32px the detail collapses into a
smear and the glyph has to carry the icon on its own.

## Headless commands

The app is a GUI, but the pipelines underneath it are verifiable without one.
These exist because driving a desktop app blindly to check whether a download
resumes is a poor use of anyone's time.

| Command | |
| --- | --- |
| `--import <path>` | Import an old database, reporting what it found |
| `--refresh-guide` | Fetch and match the XMLTV guide, reporting coverage |
| `--download <title> [--seconds N]` | Download a matching movie, then pause it |
| `--record <channel> [--seconds N]` | Record a matching live channel to `.ts` |
| `--benchmark` | Time every interaction against the real catalogs, against budgets |
| `--verbose` | Serilog debug output, plus libvlc warnings and errors |

`--download` run twice resumes from the partial file, which is the property
worth checking.

## Measured performance

Against the real provider — 68,595 movies, 11,967 channels, 27,561 programmes —
on the published `win-x64` build. Reproduce with `--benchmark`.

| Interaction | Median | Budget |
| --- | ---: | ---: |
| Start to an interactive window | 712 ms | 1500 ms |
| Load all catalogs from disk cache | 693 ms | 1500 ms |
| Open Movies (filter + sort 68,595) | 3.1 ms | 100 ms |
| One keystroke in search | 11.0 ms | 16 ms |
| Pick a category (11,189 items) | 3.4 ms | 16 ms |
| Open Live (11,967 channels) | 0.5 ms | 100 ms |
| Open the Guide (query + de-overlap) | 110 ms | 1000 ms |
| Change guide category | 0.5 ms | 16 ms |

16 ms is one frame at 60 Hz, so anything under it cannot drop a frame. The
keystroke number is the one that matters most and it started at **20.7 ms** —
over budget, and exactly the kind of lag that makes typing feel like it is
fighting you. Sorting was being redone on every keystroke even though the sort
order depends on neither the search text nor the category. Precomputing it once
per catalog took the search to 11 ms and opening Movies from 40 ms to 3 ms.

## Layout

```
app/
  src/XtreamPlayer.Core/            models, navigation, filtering, EPG timeline
  src/XtreamPlayer.Infrastructure/  SQLite, Xtream client, XMLTV, downloads
  src/XtreamPlayer.Playback/        LibVLCSharp: playback and recording
  src/XtreamPlayer.App/             Avalonia views and view models
  tests/XtreamPlayer.Tests/         362 tests, pure logic
  tests/XtreamPlayer.UiTests/       22 headless tests driving real views
  packaging/                        release script, installer, icon generator
```

The navigation engine lives in `Core`, not `App`. It is the headline feature and
has to be testable without a UI.

`XtreamPlayer.UiTests` runs the real application headlessly, so tests build real
views and tear them down. It exists because two crashes shipped from the player's
teardown — visual-tree lifecycle bugs that no amount of pure-logic testing could
reach, and that only happen when a real control is really attached and then
really removed. The harness reproduced the second one on its first run, with the
identical exception.

## Where your data lives

| | |
| --- | --- |
| Database | `%LOCALAPPDATA%\XtreamPlayer\xtream-player.sqlite` |
| Catalog cache | `%LOCALAPPDATA%\XtreamPlayer\cache` |
| Posters | `%LOCALAPPDATA%\XtreamPlayer\poster-cache` |
| Logs | `%LOCALAPPDATA%\XtreamPlayer\logs` |
| Downloads | `%USERPROFILE%\Videos\XtreamPlayer` (configurable) |

Uninstalling removes the two caches and keeps everything else.

## Things worth knowing

**Provider passwords are obfuscated, not secret.** They are encrypted at rest
with DPAPI under your Windows account, but the app must decrypt them unattended
to build stream URLs, so this is OS access control rather than a secret store.
More to the point, **the password is part of every stream URL**, which is why
logs run through a redacting enricher that rewrites credentials in path segments
and query strings across every sink, including libvlc's own output. That matters
more in practice than the encryption does.

**Recording costs a connection slot.** Each recording opens its own connection to
the provider, separate from playback, because LibVLC 3 fixes the output chain
when the media is opened — `libvlc_media_player_record()` is a 4.0 API and does
not exist in the 3.0.23 shipped here, despite appearing throughout the docs.
Accounts commonly allow two connections, so recording while watching can be your
limit. Downloads narrow to a single transfer while something is playing for the
same reason: exceeding the limit breaks the stream, not just the download.

**Guide data is frequently malformed.** Roughly 8% of programmes in a real feed
overlap the previous one on their channel. They are resolved into a clean
timeline before rendering, preferring the more specific listing where two cover
the same moment. A handful of channels publish nothing but overlapping duplicates
of one title, and those legitimately collapse to a few blocks.

**Linux needs system VLC.** There is no libvlc NuGet package for Linux; install
your distribution's `vlc` and `vlc-plugin-base`. Embedding also relies on X11, so
run under XWayland rather than native Wayland. macOS is unverified.
