# Xtream Player WPF + LibVLCSharp Build Plan

## Purpose

This document describes how to rebuild the current `xtream-player` application as a Windows-first C# desktop app using WPF and LibVLCSharp, while preserving the current product state and feature set.

This is not a greenfield product spec. It is a parity-first technical build plan that maps the current React + Tauri + Rust app into a native WPF architecture.

## Why This Stack

The current app's hardest requirement is not browsing catalogs. It is reliable in-app playback with:

- a truly embedded player surface
- multiple audio tracks
- native playback controls
- Windows packaging

That requirement fits WPF + LibVLCSharp better than a webview-hosted UI with a native playback sidecar.

Relevant current package guidance as of this document:

- `LibVLCSharp.WPF` stable package: `3.9.7.1`
- `VideoLAN.LibVLC.Windows` stable package: `3.0.23.1`
- LibVLCSharp documentation recommends `LibVLC 3.x` and `LibVLCSharp 3.x` stable builds for production.

## Current App State To Match

The WPF rebuild should reach the same functional state as the current app, including the parts that are already implemented and working today.

### Current screens

- `Dashboard`
- `Providers`
- `Live`
- `VOD`
- `Series`
- `Player`

### Current persisted data

SQLite is already used for:

- saved provider profiles
- favorites
- continue watching / resume history

Current tables in the Rust app:

- `profiles`
- `favorites`
- `resume_history`

### Current provider/backend behavior

The app currently supports:

- Xtream provider credential preview via `player_api.php`
- saving provider credentials locally
- loading live catalog
- loading VOD catalog
- loading series catalog
- loading series detail and episodes
- loading short EPG
- loading XMLTV snapshot summary

### Current browsing behavior

The app currently supports:

- dashboard global search across live, VOD, and series
- category browsing for live, VOD, and series
- favorite toggling
- hidden category toggling
- client-side filter/search within screens
- basic sort modes
- lazy poster batching for large VOD/series sets
- deep-linking from dashboard search into series detail

### Current playback behavior

The app currently supports or attempts to support:

- playback session routing to a dedicated player screen
- live playback
- VOD playback
- series playback
- episode playlists with next/previous episode navigation
- resume/checkpoint support for VOD and series
- external player handoff
- managed internal playback concepts
- preference for English audio when available
- audio track switching / cycling for native playback paths

### Current visual/product expectations

The WPF rebuild should keep the present product direction:

- dark Windows-first desktop app
- black player backgrounds
- poster-heavy VOD and series browsing
- dense browsing layout with category rail + content detail area
- top-level dashboard with search, quick actions, continue watching, and favorites

## Explicit Non-Goals

The rebuild should not spend time preserving the old implementation mechanics.

Do not preserve:

- React components
- Tauri commands
- Rust playback embedding workarounds
- browser `<video>` playback as the primary in-app engine

Preserve behavior and data shape, not the old framework boundaries.

## Recommended Solution Layout

Use a multi-project .NET solution.

```text
XtreamPlayer.sln
src/
  XtreamPlayer.App/                # WPF shell, App.xaml, MainWindow, DI bootstrap
  XtreamPlayer.Application/        # Use cases, services, commands, DTOs, interfaces
  XtreamPlayer.Domain/             # Core models and enums
  XtreamPlayer.Infrastructure/     # SQLite, Xtream HTTP client, XMLTV parsing, persistence
  XtreamPlayer.Playback/           # LibVLCSharp integration and playback coordination
  XtreamPlayer.Contracts/          # Optional shared request/response contracts
tests/
  XtreamPlayer.Application.Tests/
  XtreamPlayer.Infrastructure.Tests/
  XtreamPlayer.Playback.Tests/
```

## Target Runtime and Base Choices

- OS target: Windows 10/11
- UI: WPF on .NET 8
- Pattern: MVVM
- DI: `Microsoft.Extensions.DependencyInjection`
- Logging: `Microsoft.Extensions.Logging`
- HTTP: `HttpClientFactory`
- Persistence: SQLite via `Microsoft.Data.Sqlite` or `Dapper` + `Microsoft.Data.Sqlite`
- Playback: `LibVLCSharp` + `LibVLCSharp.WPF`
- Native VLC binaries: `VideoLAN.LibVLC.Windows`
- Packaging: MSIX or self-contained installer after parity is reached

## NuGet Packages

Recommended initial package set:

- `LibVLCSharp.WPF`
- `VideoLAN.LibVLC.Windows`
- `CommunityToolkit.Mvvm`
- `Microsoft.Extensions.DependencyInjection`
- `Microsoft.Extensions.Hosting`
- `Microsoft.Extensions.Http`
- `Microsoft.Extensions.Logging`
- `Microsoft.Data.Sqlite`
- `Dapper` (optional but useful)
- `System.Text.Json`

## High-Level Architecture

### 1. App shell

The WPF app should have one main window with:

- navigation rail or top navigation
- content region for Dashboard / Providers / Live / VOD / Series / Player
- global status/toast host
- shared selected-profile state

### 2. Application layer

This layer coordinates:

- bootstrapping startup state
- provider login preview and save
- catalog sync
- favorites and resume updates
- playback session state
- navigation state

### 3. Infrastructure layer

This layer replaces the current Rust backend:

- Xtream HTTP client
- XMLTV parsing
- SQLite repositories
- provider credential persistence

### 4. Playback layer

This layer owns:

- `LibVLC` initialization
- `MediaPlayer` lifecycle
- embedded playback into WPF `VideoView`
- audio/subtitle track enumeration
- English-audio preference logic
- series episode playlist navigation
- pause/seek/stop/fullscreen state

## View and ViewModel Map

Build the WPF UI using one view and one viewmodel per current screen.

### Shell

- `MainWindow.xaml`
- `MainWindowViewModel`

Responsibilities:

- top-level navigation
- selected provider
- current screen viewmodel host
- startup bootstrap load
- global status messages

### Dashboard

- `DashboardView.xaml`
- `DashboardViewModel`

Responsibilities:

- quick stats for live/VOD/series/profiles
- global search across loaded catalogs
- continue watching list
- favorites list
- shortcuts into other sections

### Providers

- `ProvidersView.xaml`
- `ProvidersViewModel`

Responsibilities:

- create new provider profile
- select existing profile
- preview provider credentials
- save provider to SQLite
- show account status, formats, suggested endpoints

### Live

- `LiveView.xaml`
- `LiveViewModel`

Responsibilities:

- live category rail
- favorites pseudo-category
- all-channels pseudo-category
- hidden category support
- short EPG loading
- play selected channel

### VOD

- `VodView.xaml`
- `VodViewModel`

Responsibilities:

- genre/category filtering
- poster grid
- sorting by recent/title
- favorites toggling
- large-list incremental rendering
- play selected movie

### Series

- `SeriesView.xaml`
- `SeriesViewModel`

Responsibilities:

- category rail
- series poster list
- series detail load on selection
- season selection
- episode list
- play selected episode
- construct episode playlist for next/previous

### Player

- `PlayerView.xaml`
- `PlayerViewModel`

Responsibilities:

- embedded `VideoView`
- title / metadata
- play / pause / stop / seek
- audio track picker
- subtitle track picker if exposed
- next/previous episode
- external player handoff
- save resume checkpoints

## Domain Model Port

The WPF version should keep the same conceptual models now used in `src/types/xtream.ts`.

Port these as C# records or classes:

- `XtreamProfile`
- `ProviderPreview`
- `LiveChannel`
- `VodItem`
- `SeriesItem`
- `SeriesDetail`
- `SeriesSeason`
- `SeriesEpisode`
- `FavoriteItem`
- `ContinueWatchingItem`
- `PlaybackSession`
- `EpisodePlaylistEntry`
- `EpgListing`
- `XmltvSnapshot`
- `PlayerStatus`

Recommended namespaces:

- `XtreamPlayer.Domain.Models`
- `XtreamPlayer.Application.Contracts`

## SQLite Port

Keep the existing schema initially to minimize migration risk.

### Tables to preserve

```sql
profiles
favorites
resume_history
```

### Proposed repository interfaces

```csharp
public interface IProfileRepository
public interface IFavoritesRepository
public interface IResumeHistoryRepository
```

### Suggested repository methods

- `GetProfilesAsync()`
- `GetProfileCredentialsAsync(profileId)`
- `SaveProfileAsync(request, status, expiresAt, maxConnections)`
- `GetFavoritesAsync()`
- `AddFavoriteAsync(profileId, contentId, title, kind)`
- `RemoveFavoriteAsync(favoriteId)`
- `GetResumeHistoryAsync()`
- `SaveResumeCheckpointAsync(...)`

### Migration strategy

- reuse `xtream-player.sqlite` as the database filename
- preserve table names
- preserve column names where practical
- add schema versioning table for the C# app moving forward

## Xtream API Port

Port the Rust Xtream client behavior into a C# service.

### Suggested services

- `IXtreamClientFactory`
- `IXtreamClient`
- `ICatalogService`
- `IProviderPreviewService`
- `IXmlTvService`

### Endpoints to support

- `player_api.php`
- `player_api.php?action=get_live_categories`
- `player_api.php?action=get_live_streams`
- `player_api.php?action=get_vod_categories`
- `player_api.php?action=get_vod_streams`
- `player_api.php?action=get_series_categories`
- `player_api.php?action=get_series`
- `player_api.php?action=get_series_info`
- `player_api.php?action=get_short_epg`
- `xmltv.php`

### Behavior to preserve

- normalize server URL to include scheme if missing
- authenticate before catalog use when needed
- derive stream URLs using Xtream username/password path style
- decode EPG base64 text when needed
- choose preferred live output extension from allowed formats
- prefer `direct_source` for series episodes when provided

## Playback Architecture

This is the main architectural change from the current app.

### Core principle

Do not build a browser playback path first.

Build a single native playback service around `LibVLCSharp` and use that as the app's primary in-window player.

### Recommended playback objects

- singleton or app-scoped `LibVLC`
- session-scoped `MediaPlayer`
- player view binds to `VideoView.MediaPlayer`

### WPF control guidance

LibVLCSharp's documented WPF approach uses a Win32-hosted surface under the hood. This is acceptable here because the app is Windows-first and needs reliable embedded playback.

Important documented caveat:

- WPF has airspace issues for overlays on hosted native video
- if overlay controls are needed over the video, place overlay content inside the `VideoView`
- use a non-fully-transparent background for overlay click hit-testing when needed

### Playback service interface

```csharp
public interface IPlaybackService
{
    PlayerStatus CurrentStatus { get; }
    Task PlayAsync(PlaybackSession session);
    Task PauseAsync();
    Task ResumeAsync();
    Task StopAsync();
    Task SeekAsync(TimeSpan position);
    Task NextAudioTrackAsync();
    Task SetAudioTrackAsync(int trackId);
    Task<IReadOnlyList<AudioTrackOption>> GetAudioTracksAsync();
    Task OpenExternalPlayerAsync(string streamUrl);
}
```

### Playback behaviors to implement

- embedded playback for live, VOD, series
- English audio preference on load
- enumerate available audio tracks and expose labels/languages
- next/previous episode navigation from playlist
- save playback progress for VOD and series
- external player fallback button

### Suggested playback events to surface

- media opened
- playback started
- paused
- stopped
- buffering changed
- time changed
- end reached
- audio track list changed
- error occurred

## State Management in WPF

Use MVVM state instead of a browser-global reducer.

### Suggested state containers

- `AppStateStore`
- `PlaybackStateStore`
- `CatalogStateStore`

These can be simple DI services with observable properties and event notifications.

### ViewModel base

Use `CommunityToolkit.Mvvm`:

- `ObservableObject`
- `RelayCommand`
- `AsyncRelayCommand`

## Current Feature Parity Checklist

The C# rebuild should reach these checkpoints before it is considered at parity.

### Startup and shell

- load saved bootstrap state from SQLite at app start
- auto-select the first saved profile if none selected
- preserve dashboard-first navigation

### Providers

- preview provider credentials
- save provider profile
- select existing provider
- persist credentials locally

### Dashboard

- quick counts for live/VOD/series/profiles
- cross-catalog search
- continue watching panel
- favorites panel

### Live

- sync live catalog
- category filtering
- favorites category
- all channels category
- hidden categories
- short EPG fetch for selected channel
- play selected live channel

### VOD

- sync VOD catalog
- poster grid
- sorting
- filtering
- hidden genres
- favorites toggling
- play selected item

### Series

- sync series catalog
- series detail fetch
- season selection
- episode list
- play selected episode
- episode playlist navigation

### Player

- embedded playback
- play/pause/stop/seek
- native audio track selection
- English-audio preference
- next/previous episode
- external player launch
- resume checkpoint save

### Persistence

- save favorites
- save continue watching
- read/write provider profiles

## Suggested Build Phases

### Phase 1: Solution skeleton

- create solution and projects
- add DI and app bootstrap
- create domain models
- create repositories and migrations
- verify SQLite opens and seeds nothing unexpected

### Phase 2: Infrastructure parity

- implement Xtream HTTP client
- implement provider preview
- implement catalog loaders
- implement XMLTV snapshot parsing
- port series detail mapping

### Phase 3: Non-player UI parity

- build shell and navigation
- build Providers screen
- build Dashboard screen
- build Live / VOD / Series screens
- wire favorites and hidden categories

### Phase 4: Playback parity

- integrate `VideoView`
- implement `PlaybackService`
- implement audio track enumeration and switching
- implement episode playlist navigation
- implement resume checkpointing

### Phase 5: Packaging and hardening

- Windows publish profile
- installer or MSIX
- logging and crash handling
- startup/load-time improvements

## Recommended Publishing Approach

For the first stable parity build:

- target `win-x64`
- publish self-contained if you want fewer runtime prerequisites
- bundle `VideoLAN.LibVLC.Windows` assets with the app
- verify native VLC binaries are copied to the final output

Example publish target:

```powershell
dotnet publish .\src\XtreamPlayer.App\XtreamPlayer.App.csproj -c Release -r win-x64 --self-contained true
```

## Risks and Design Notes

### 1. Airspace/overlay behavior in WPF

LibVLCSharp.WPF documents the WPF airspace issue. This is manageable, but the player view should be designed with native-hosted video in mind.

### 2. Large catalog rendering

The current app already batches poster-heavy lists. The WPF rebuild should use virtualization and incremental loading rather than rendering all posters at once.

### 3. Provider variability

Xtream responses are inconsistent across providers. Keep mapping logic defensive, preserve null-tolerant parsing, and centralize response normalization.

### 4. Resume semantics

The current app stores resume as `progress_percent`, not absolute position. Preserve that initially for compatibility, then optionally add absolute position later.

### 5. Credentials storage

The current SQLite schema stores passwords plainly. If this app is being hardened during the rebuild, add Windows DPAPI or Credential Locker integration. If parity is the only goal, preserve behavior first and harden in a follow-up phase.

## Suggested Initial Implementation Decisions

If this rebuild starts now, these are the decisions to lock early:

- use `.NET 8` WPF
- use `CommunityToolkit.Mvvm`
- use `LibVLCSharp.WPF` + `VideoLAN.LibVLC.Windows`
- reuse the existing SQLite schema first
- port the Rust Xtream logic into `Infrastructure`
- build one native playback path only
- keep Windows as the primary supported platform

## Minimal Example of The Player Direction

At the UI level, the player should conceptually look like this:

```xml
<vlc:VideoView MediaPlayer="{Binding MediaPlayer}">
    <Grid Background="#02000000">
        <!-- Overlay controls hosted inside the VideoView -->
    </Grid>
</vlc:VideoView>
```

At startup, the playback layer should conceptually do this:

```csharp
Core.Initialize();
_libVlc = new LibVLC();
_mediaPlayer = new MediaPlayer(_libVlc);
```

If using the native VLC NuGet packages in standard locations, explicit `Core.Initialize()` path configuration may not be required, but it can still be called deliberately during app startup.

## Deliverable Definition

The WPF rebuild is functionally successful when a user can:

- save an Xtream provider
- browse live, VOD, and series catalogs
- search across the loaded catalog
- mark favorites
- resume VOD and episodes
- play streams inside the app window with LibVLCSharp
- switch audio tracks in the embedded player
- move between episodes in a series playlist
- fall back to an external player when needed

That is the parity target for the new stack.