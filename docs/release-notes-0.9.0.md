# Xtream Player 0.9.0

A complete rewrite. The old build was React, Vite and Tauri; this one is C#,
Avalonia and LibVLC, and shares no code with it.

**This is a beta, and the number is deliberate.** Everything here is built and
tested, but the parts that need real hardware and a real provider — live TV,
audio and subtitle switching, memory over a long session — have not yet been
through anyone's hands but the author's. That is what this round is for.

## Why it was rewritten

Three things about the old build were not fixable in place.

**There was no navigation.** Every change of view unmounted the previous page,
destroying the filter text, the selected category, the selected item and the
scroll position. Nothing restored scroll; nothing was meant to.

**Playback was three half-finished engines, none of which worked.** The HTML5
path could not decode raw MPEG-TS, which is what most live channels are. A
hand-rolled libvlc integration existed in full and was called from nowhere. The
third spawned an external mpv window.

**Several features existed only as backends.** Resume was implemented end to end
in Rust and called from nowhere, so Continue Watching never populated. The XMLTV
guide was parsed only to count its elements; every channel's current programme
was the literal string "Use short EPG sync for now/next".

## What this one does

**Navigation that remembers where you were.** Going back returns you to the exact
category, filter text, selection and scroll offset — including after closing the
player. This is the headline feature and everything else was built around it.

**One video engine.** Live MPEG-TS, HLS, VOD and local files through the same
embedded player, with audio and subtitle track switching and external `.srt`
loading. No second window, no external process.

**A real XMLTV guide.** A drawn timeline grid rather than a control per
programme, which is what makes a thousand channels over thirty hours possible at
all.

**Resume that works**, a downloads queue that survives a restart, and live
recording to disk independent of what you are watching.

**Favorites and hidden categories** carried over, per profile, with the
collisions the old schema allowed now impossible.

## Measured

On a real provider with 11,967 channels, 68,595 movies and 27,561 programmes:

| | |
| --- | ---: |
| Start to an interactive window | 712 ms |
| Open Movies, filtering 68,595 items | 3.1 ms |
| One keystroke in search | 11.0 ms |
| Open the Guide | 110 ms |

16 ms is one frame at 60 Hz. Reproduce any of it with `--benchmark`.

## Known limits

- **The installer is not signed**, so SmartScreen will warn. More info → Run
  anyway.
- **Windows only, in practice.** It builds for Linux and macOS and neither has
  been verified. Linux additionally needs a system VLC, and X11 rather than
  native Wayland.
- **Recording costs a provider connection slot**, so recording while watching may
  hit your account's limit.
- **Guide data is often malformed at source.** Overlapping listings are resolved
  before drawing; a few channels publish nothing but duplicates of one title and
  those legitimately collapse.

## If something breaks

Send `%LOCALAPPDATA%\XtreamPlayer\logs`. `crash.log` holds the stack of anything
that killed the app. Credentials are stripped before anything is written, so the
logs are safe to share.

Saying what you were doing helps more than it might seem: the bugs that survive
to a release tend to be about sequence rather than any one screen.
