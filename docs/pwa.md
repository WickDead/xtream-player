# iPhone PWA: direct IPTV playback

The PWA connects directly to your IPTV provider. It does **not** require the optional
ASP.NET backend, Docker, device pairing, FFmpeg, or another hosted service.
GitHub Pages hosts only the static application.

## Try it locally

```powershell
dotnet run --project src/XtreamPlayer.Web --no-launch-profile --urls http://localhost:5081
```

Open http://localhost:5081, go to Settings, and enter the provider's HTTPS base URL,
username and password. Choose Live TV, Movies or Series, then play a title.

Provider credentials are saved in this browser's IndexedDB so the browser can contact
the provider directly. This is browser storage, not desktop DPAPI encryption.
Favorites, progress, hidden categories and cached catalogs remain device-local.
Do not put credentials into source files, GitHub Actions, URLs for the app, or Pages configuration.

## Browser playback

- Safari and Chrome on iPhone can open the PWA. Both support Home Screen shortcuts;
  use Share → Add to Home Screen. See [Chrome's iPhone instructions](https://support.google.com/chrome/answer/9658361?co=GENIE.Platform%3DiOS&hl=en).
- Live TV requests the provider's HLS (.m3u8) stream. Native HLS is preferred.
  A bundled hls.js fallback supports browsers with MediaSource but no native HLS.
- Movies and episodes use the provider's original URLs and container formats.
  There is no server conversion. Changing a filename extension does not convert media.
- Native video controls provide seeking, fullscreen, volume, and available tracks.
  Resume uses the actual media position on this device.
- The XMLTV guide and provider now/next data are fetched directly. Guide gap estimates
  use the same channel-name logic as desktop. Catch-up uses the provider's archive URL;
  TS-only archive streams may not play in a browser.
- Recording, downloads, offline video, external sports enrichment and TVMaze discovery
  are absent from this simplified PWA. Desktop features remain available.
- The app shell and saved catalogs can open offline; streaming needs a connection.

The provider must allow browser API requests (CORS) and offer HTTPS APIs, artwork and
media. HTTPS redirects and segments must also be browser-compatible. Switching from
Safari to Chrome does not bypass those requirements. Playback on an actual iPhone and
your real provider still needs acceptance testing.

## Shared code

Application now owns the browser-safe Xtream client, URL builder, JSON converters and
XMLTV parser as well as the browse workflow. The extracted provider files retain their
existing namespaces for source compatibility. Infrastructure references Application;
the browser never references SQLite, DPAPI or LibVLC.

The current frontend uses DirectProviderClient, not the optional backend API client.
Backend code and its packaging remain in the repository for future use, but are not
started or contacted by this PWA. [Earlier backend architecture](pwa-backend.md) is
retained as historical/reference documentation, not the current frontend setup.

## GitHub Pages

1. Select GitHub Actions as the repository's Pages source.
2. Run the **Publish iPhone PWA** workflow.
3. Open the resulting URL and add the provider in Settings.

```sh
node packaging/publish-pwa.mjs /xtream-player/
```

The publish script creates `dist/pwa/wwwroot` with subpath-safe assets, hash navigation,
and an application-shell service worker. Use `/` for a custom domain or account Pages site.
Updates preserve device data and prompt before reloading during playback.

## Checks

```sh
dotnet test XtreamPlayer.slnx
node --test tests/web/*.test.cjs
node packaging/publish-pwa.mjs /xtream-player/
```

Direct-provider tests cover login persistence/removal, failed login, URL validation,
HLS selection, movie and episode URLs, and browser-side resume offsets. Existing desktop
tests cover the extracted provider parser and guide behavior.

For a generated local provider, create `dist/test-tools/fixture.mp4` and HLS files at
`dist/test-tools/hls/index.m3u8`, then run `node tests/web/direct-provider-fixture.mjs`.
It binds only to 127.0.0.1:5094 and accepts arbitrary test credentials. It is never
included in the deployed app.


Local validation (2026-10-10): 579 .NET tests passed (11 skipped, including the
optional backend FFmpeg smoke test); 10 JavaScript tests passed. The published
subpath build successfully connected to a generated local provider and played
HLS and MP4 directly in Chrome. Real-provider and real-iPhone acceptance remain.
