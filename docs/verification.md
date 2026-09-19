# Manual verification

Tests cover the pure logic. These are the things that can only be checked by
using the app, kept as checklists so they are run the same way each time.

Most of them have a headless shortcut — see the table in the [README](../README.md).
Use it. Driving a GUI by hand to find out whether a download resumes is a poor
trade when a command answers the same question in twenty seconds.

## Before calling a build good

- [ ] `dotnet test app/XtreamPlayer.slnx` — all green
- [ ] `pwsh app/packaging/build-release.ps1` — publishes, trims, smoke-tests
- [ ] Launch the published exe, not just `dotnet run`. A release build differs in
      output type, trimming and ReadyToRun, and all three have broken startup before.

## Playback matrix

For each of: **live TS**, **live HLS**, **VOD mp4**, **VOD mkv**, **an episode
with several audio tracks**, **a downloaded local file**:

- [ ] Plays within a few seconds
- [ ] "Buffering…" clears once the picture is up *(regression: the final
      `Cache == 100` event was being discarded, so the overlay sat on top of
      video that had been playing for a minute)*
- [ ] Pause and resume
- [ ] Seek forward and back — and the seek bar is **absent** for live
- [ ] Switch audio track; switch subtitle track; load an external `.srt`
- [ ] Fullscreen in and out; Escape exits fullscreen before it goes Back
- [ ] Closing the player returns to the exact list position

Then **ten open/close cycles** on one title, watching memory return to baseline
with no orphaned libvlc threads. Native wrappers leak somewhere eventually, and
this is how it gets found.

## Navigation

A fixed route, checking at every Back that category, filter text, selection and
scroll offset all survive:

```
Dashboard → Movies → scroll to ~5000 → open detail → Back
          → Series → season 2 → episode → Player → Back → Back
          → Live → category → channel → Guide → Back
          → Dashboard
```

- [ ] Run it once maximised
- [ ] Run it again, resizing the window **while on a detail page**, then Back —
      the offset must be recomputed from the item index, not replayed in pixels
- [ ] `Alt+Left`, mouse button 4, and the breadcrumb all behave identically
- [ ] Backspace goes Back — but **not** while the caret is in a search box

## Guide

- [ ] Renders and scrolls smoothly at the real channel count
- [ ] No overlapping programme blocks, and no title drawn over another
- [ ] Changing category re-filters instantly, with no reload
- [ ] The now-line sits where it should and moves
- [ ] A channel with no listings shows "No guide data" rather than an empty row

## Downloads and recording

- [ ] `--download "<title>"` twice — the second run resumes from the partial file
- [ ] Kill the app mid-download, restart: it resumes from the file's real length,
      not the last progress row
- [ ] Cancel keeps the `.part`; Remove deletes it
- [ ] A completed download plays locally, with resume and track switching
- [ ] `--record "<channel>"` produces a `.ts` that grows and then plays
- [ ] Recording keeps running after navigating away from the player

If a recording writes zero bytes, check the provider before the code. Half the
channels on a typical account are dead at any moment, and the provider answers
with odd HTTP codes (`407`, `454`) rather than anything honest. `--record
--verbose` prints the access module's status line.

## Performance

`--benchmark` times each interaction against the real catalogs and fails if any
exceeds its budget. Run it before calling a build good; a regression here is
invisible in tests and obvious in use.

- [ ] `XtreamPlayer.exe --benchmark` — exit code 0
- [ ] Startup: launch the published build and check the log for
      `Window shown N ms after process start`. Baseline is ~712 ms.

Budgets are 16 ms for anything on a typing or selection path (one frame at
60 Hz), 100 ms for opening a page, and 1000-1500 ms for work that only happens
once at startup. The numbers to compare against are in the
[README](../README.md#measured-performance).

## Soak

- [ ] Leave a live channel playing for four hours. Check reconnects, memory
      growth, and that the resume and EPG timers have not wedged the UI thread.

## Multi-profile isolation

Two profiles whose content ids overlap — they will, since ids are small integers
from the same namespace:

- [ ] Favorites do not leak between profiles
- [ ] Resume history does not leak between profiles
- [ ] A live channel and a movie sharing an id can both be favorited
- [ ] Switching profile resets the navigation stack cleanly

## Credentials

- [ ] Grep the log directory for the provider password. It must not appear —
      not in app logs, not in libvlc output, not in an error surfaced to the UI.
      The password is part of every stream URL, so this is the control that
      actually matters.
