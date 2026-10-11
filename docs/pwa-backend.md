# iPhone PWA and private backend

The PWA is a separate frontend in this solution, sharing C# behavior with the desktop app.
Its static files can live on GitHub Pages. The private backend runs elsewhere and is required
for provider access, online playback, downloads, and recording. Favorites, hidden categories,
navigation state, and watch progress are local to each browser installation.

## Projects and responsibilities

- **Core**: catalog models, filtering, ordering, resume rules, category reconciliation, EPG matching, sports matching.
- **Application**: shared browse workflow, browser-safe API contracts, storage/playback/job interfaces.
- **App**: Avalonia UI, SQLite/DPAPI, LibVLC playback and local desktop jobs.
- **Web**: Blazor WebAssembly, IndexedDB library data, native Safari video, OPFS offline media.
- **Server**: ASP.NET Core API, encrypted SQLite records, guarded provider access, FFmpeg, persistent jobs.

The desktop browse pages and PWA use the same `BrowseWorkflow`. UI changes still need
implementation in both frontends. Use the PR checklist; don't copy business rules into Razor
components or Avalonia views when they can live in the shared libraries.

## Run locally

Requires .NET 10. Node 22+ is used by publishing and JavaScript tests.
Install FFmpeg and ffprobe on the server machine; both must be on PATH or configured explicitly.

PowerShell, in the repository root:

```powershell
$env:Server__PairingCode = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:Server__DataDirectory = Join-Path $PWD 'dist/server-dev'
$env:Server__PublicUrl = 'http://localhost:5080'
$env:Server__InternalUrl = 'http://127.0.0.1:5080'
$env:Server__AllowedOrigins__0 = 'http://localhost:5081'
dotnet run --project src/XtreamPlayer.Server --no-launch-profile --urls http://127.0.0.1:5080
```

Save the generated pairing code privately before launching the server. In another terminal:

```powershell
dotnet run --project src/XtreamPlayer.Web --no-launch-profile --urls http://localhost:5081
```

Open the web app's Settings, enter the backend URL and pairing code, then add your provider.
The provider is authenticated before it is retained. The browser never receives its password
or upstream stream URLs. A desktop provider must be added separately; there is no automatic sync.

Local HTTP works only on localhost. An actual iPhone needs a reachable HTTPS deployment.
Online HLS playback targets Safari; desktop Chromium's native HLS support varies. Offline MP4
playback is usable in compatible desktop browsers too.

## Deploy the backend

1. Choose a Linux Docker host with persistent disk space and enough CPU for conversion.
2. Point a backend hostname at it and allow inbound TCP 80/443 (UDP 443 is optional).
3. Copy `packaging/server/.env.example` to `packaging/server/.env`.
4. Set `SERVER_HOST`, `PWA_ORIGIN`, and a random `PAIRING_CODE` of at least 32 characters.
   The origin is, for example, `https://your-account.github.io`, without the repository path.
5. Start it:

```sh
docker compose --env-file packaging/server/.env -f packaging/server/compose.yaml up -d --build
```

Caddy obtains the HTTPS certificate. Only Caddy publishes host ports; the backend stays on the
Docker network. The backend runs as the non-root `app` user. Named volumes persist media, the
database, encryption keys, and Caddy certificates. **Back up the server-data volume including
its keys**; the encrypted database cannot be restored without them. Don't commit the .env file.

Default limits: 100 GiB of media, one video transcode, and a 512 MiB free-disk reserve.
Source downloads plus prepared copies temporarily need roughly twice a title's size.
A single download/recording worker runs at a time. Playback has separate leases against the
provider's reported connection limit; independent desktop usage cannot be counted reliably.
CPU capacity and provider compatibility must be verified on the chosen server before normal use.

The provider hostname is automatically permitted. Additional redirect/CDN/artwork hosts must
be explicitly listed on the backend, for example in Compose:

```yaml
Server__AllowedUpstreamHosts__0: media.provider.example
Server__AllowedUpstreamHosts__1: images.provider.example
```

Use hostnames only, without schemes or paths. Every redirect is checked; DNS connections reject
private, loopback, link-local and other non-public destinations. Local-network providers are
intentionally unsupported by this internet-facing backend. The API does not accept arbitrary
media URLs from clients. FFmpeg receives local relay URLs and a restricted input protocol list.
HLS manifests with variable substitution are rejected rather than interpreted unsafely.

The pairing code is the owner's bootstrap secret. Paired devices have independent revocable
tokens; Settings lists/revokes them. Revoke a lost device and rotate the bootstrap code if it
was disclosed. Signed playback/file URLs are short-lived credentials, so access logging is
disabled in the supplied proxy and URI logging is disabled in the backend. Proxy error logs
also remove request/header fields using Caddy's
[log filtering](https://caddyserver.com/docs/caddyfile/directives/log#filter).

`GET /health` checks application liveness. Verify a sample playback and download as the
media-engine readiness check. A healthy HTTP endpoint alone doesn't prove codec compatibility.

## Publish to GitHub Pages

1. Commit the application source to the intended GitHub repository.
2. In repository Settings → Pages, select **GitHub Actions** as the source.
3. Run the **Publish iPhone PWA** workflow. It reads the Pages base path automatically.
4. Set the backend's allowed origin to the resulting Pages origin.
5. On iPhone, open the URL in Safari → Share → Add to Home Screen.

The workflow is manually triggered so an ordinary source push doesn't publish unexpectedly.
It uploads only the static PWA. No provider credentials or backend pairing secrets belong in
GitHub Pages, GitHub Actions variables, or frontend configuration.

To build the exact static artifact locally:

```sh
node packaging/publish-pwa.mjs /xtream-player/
```

Use `/` for a custom domain or user/organization Pages site. Output is `dist/pwa/wwwroot`.
The publisher changes the base path and recomputes the index hash and service-worker manifest
version. Hash navigation avoids the need for server rewrites. Never edit published assets after
this step without regenerating their manifest hashes.

## Playback, jobs and offline behavior

| Capability | Desktop | iPhone PWA |
| --- | --- | --- |
| Catalog, search, categories, favorites | Shared rules, desktop UI | Shared rules, touch UI |
| Resume / continue watching | Local SQLite | Device-local IndexedDB |
| Guide, catch-up, sports and new episodes | Existing services | Same provider/parser/matching code via backend |
| Playback | LibVLC | Native HLS; backend remux/conversion |
| Audio and subtitles | VLC tracks | Backend track selection; text → WebVTT, bitmap → burn-in |
| Downloads / recording | Local computer | Always-on backend queue |
| Offline copies | Local files | Prepared MP4 in OPFS |
| Local media | Filesystem picker | Browser file picker; browser-supported codecs |
| Volume / decoder configuration | Desktop controls | iPhone system volume / server conversion |

“Download to server” keeps running when the phone closes. “Keep offline” copies a completed
title into this installation. Phone transfers use 4 MiB verified range chunks, commit progress
after each chunk, and pause when the app is backgrounded. Resume an interrupted phone transfer
by choosing Keep offline again on the same completed server job.

The server creates H.264/AAC MP4 copies at up to 720p. Selected text subtitles are embedded in
the prepared MP4; bitmap subtitles are rendered into the video. Select audio/subtitles in the
player before downloading that title if you want those selections in the offline copy.
Live recordings have a bounded duration; “Finish recording” retains and prepares the partial
recording. Interrupted recordings can be recovered. Pausing conversion preserves the downloaded
source but restarts conversion. HLS downloads restart their source capture; byte-file downloads
resume only when the provider supplies a usable validator.

The offline service worker streams byte ranges rather than loading an entire movie into RAM.
It refuses incomplete or missing files. The storage page reports estimated usage and can request
persistence, but iOS controls quotas and may remove data. Retain server copies for recovery.
Removing a phone copy does not remove its server copy; deleting a server job does not delete
an already completed phone copy.

Updates use a separate application-shell cache. They preserve IndexedDB and OPFS, and require
an explicit update action while playback and offline transfer are paused.
No claim is made for native background downloading, DRM playback, every VLC codec, or automatic
cross-device synchronization.

## Verification

```sh
dotnet test XtreamPlayer.slnx
node --test tests/web/*.test.cjs
node packaging/publish-pwa.mjs /xtream-player/
```

Set `XTREAM_TEST_FFMPEG` and `XTREAM_TEST_FFPROBE` to run the real generated-media smoke test
in WebTests. It exercises a guarded HTTP relay, provider-style HLS, track detection, HLS
subtitle output, recording, preparation of an offline MP4, validated download resumption,
interrupted-recording recovery, and connection/disk limits without any real account.

For an actual browser check, generate a short MP4 fixture in `dist/test-tools/fixture.mp4`,
then run:

```sh
node tests/web/serve-preview.mjs 5092 dist/test-tools/fixture.mp4
```

Open `http://127.0.0.1:5092/xtream-player/harness.html` in a fresh browser origin, run the
checks, and follow the link to the offline library. Stop the preview server, reload, and play
the saved video. The harness is development-only and is never included in Pages output.

Automated tests cover authentication/revocation, CORS, ticket scope/expiration/tampering,
credential-free public DTOs, encrypted persistence, shared browse behavior, upstream address
restrictions, HLS rewriting, offline byte ranges/missing-file behavior, chunk resumption,
server-file replacement, quota errors, and interrupted requests.

Local verification on 2026-10-10: 575 .NET tests passed (10 existing tests skipped), including
the real FFmpeg test; all 8 JavaScript tests passed. The Release Pages artifact was tested at
a repository subpath in Chromium: service-worker installation, OPFS download, native MP4
playback and seeking, and cold offline reload after stopping the HTTP server all worked.
The 390 × 844 responsive layout was inspected. These checks do not substitute for Safari.

Docker is packaged and CI includes a container health check, but the local Docker daemon was
unavailable, so container startup/persistence and production HTTPS have not been executed
locally. No GitHub Pages or backend deployment has been made. No real provider credentials
or iPhone were used in these checks.

Before calling an iPhone release verified, test on real iOS 17+ hardware:

- Home Screen installation, app relaunch, rotation, safe areas and fullscreen.
- Your provider's live HLS, MKV/unsupported-codec conversion, audio and subtitles.
- Catch-up, guide times, provider connection limits, and long sessions.
- Background/foreground transitions and network switching.
- Interrupted Keep offline transfer; airplane-mode cold launch, playback and seeking.
- Storage exhaustion/eviction and an app update that preserves offline media.
- Backend restart during a download and recording, plus recovery and disk limits.
