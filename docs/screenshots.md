# Screenshots

How the images in the README are taken, and in particular how to get a macOS shot
and a Windows shot that carry **the same data**, so the two can be combined into
one picture.

There are two kinds of image in `docs/screenshots/`:

| kind | how it is made |
| --- | --- |
| **Windows** (`window-*.png`, `taskbar-widget*.png`, `tray-*.png`, `themes/`, …) | Rendered by `ScreenshotGenerator` in the test suite, on Windows: `$env:CLAUDESTATUS_WRITE_SCREENSHOTS = "$PWD/docs/screenshots"` then `dotnet test tests/ClaudeStatus.App.Tests --filter-method '*Regenerates_the_readme*'` (stop the running app first; it locks the build output). The renders carry the scripted day's numbers and sessions already, at a fixed instant, so only what actually changed differs from the committed image. Never regenerate them on a Mac: the fonts substitute and every image changes without any content changing. |
| **macOS** (`macos-*.png`) | Taken by hand from the running app, because the menu bar item is drawn by AppKit and nothing in the test suite can reach it. |

Both need the app to be showing invented numbers rather than the machine's real
usage — otherwise the two halves of a combined picture disagree, and a real
reading is somebody's account data.

## The scripted day

Turn **Config → Behaviour → fake data** on. That alone gives a healthy day. For a
paired shoot also set the scenario:

```bash
# macOS / Linux
export CLAUDESTATUS_FAKE_SCENARIO=Screenshot
```

```powershell
# Windows
$env:CLAUDESTATUS_FAKE_SCENARIO = 'Screenshot'
```

The variable is read when the provider is built, so set it **before launching the
app** — from the terminal you launch it from, or, on macOS, with
`open --env` (see below). Anything unrecognised falls back to the healthy day.

Both machines then show exactly:

| | |
| --- | --- |
| session (5 h) | **29 %**, resetting in **2:37** |
| week (7 d) | **58 %** |
| week, Fable | **44 %** — only where the Fable column is switched on |

`Screenshot` is `Healthy` with one difference, and it is the whole reason the
scenario exists: the session window's reset carries fifty seconds of slack, so
the countdown reads `(2:37)` for all but a few seconds of each poll rather than
sliding to `(2:36)` halfway through the shoot. It cannot be made exact — a
countdown written to the minute and a one-minute poll are the same width — so:

> **Hit Refresh, then take the shot.** That closes the gap entirely. If one of
> your two images reads `(2:36)`, refresh and retake it.

## The scripted sessions

The session badge needs sessions, and they have to be the same three on both
machines. `build/seed-screenshot-sessions.ps1` writes them straight into the
spool — the same drop box Claude Code's hooks use — so no real Claude Code
session is needed and nothing is asked of the machine's own work:

```bash
pwsh ./build/seed-screenshot-sessions.ps1
```

The app must already be running with **session watch on**. Within about twenty
seconds you get:

- **three sessions open, two working** — so the badge reads `2/3` and the working
  dots run;
- the same three names in the list, `claude-status`, `notes` and `site`, because
  only the last path segment is ever displayed and the script varies the root per
  platform, not the leaf.

Put it back when you are done:

```bash
pwsh ./build/seed-screenshot-sessions.ps1 -Clear
```

The **durations** in the details popup ("open 41m") keep ticking after the seed,
so if the combined picture includes a popup, take both shots within a minute or
two of seeding. The indicators themselves show only the counts and do not drift.

## Taking the macOS shots

`screencapture` does capture the menu bar, despite an older note in
`CHANGELOG-dev.md` to the contrary — that note is about notification *banners*,
which Notification Center does hide from it.

```bash
# the whole strip, to find where the item sits
screencapture -x -R1300,0,500,26 /tmp/find.png

# then the item itself, at the size the README uses
screencapture -x -R<x>,0,420,26 docs/screenshots/macos-menu-bar-working.png
```

- `-x` keeps it silent, so the shutter sound is not part of the shoot.
- The existing images are **420 × 26**, framed with the item at the left and a few
  system icons at the right. Match that, or the README's `width="420"` will
  rescale and soften it.
- `macos-menu-bar.png` is taken with the session watch **off**, so that image
  shows what its section is about: the reading as text. `macos-menu-bar-working.png`
  is taken with the watch on and a turn in progress.
- Notification banners need ⌘⇧4, Space, click. A macOS "background activity"
  banner likes to sit exactly where ours appears; dismiss it first.

## Combining the two

Take the macOS images and the Windows ones with the same scenario and the same
seeded sessions, and they will carry the same numbers, the same countdown and the
same three project names. The only deliberate differences left are the ones worth
showing: the shape of the indicator, and the system furniture around it.

## When the app is not installed

For a throwaway macOS build you can point the app at the scenario without
touching your shell profile:

```bash
open --env CLAUDESTATUS_FAKE_SCENARIO=Screenshot -a /path/to/ClaudeStatus.app
```

Remember that a build run from outside `/Applications` still writes its hooks
into `~/.claude/settings.json` while it runs, and still registers a login item if
autostart is on. Quit it from its own menu when you are done — that removes the
hooks — and check **System Settings → General → Login Items** if you left
autostart on.
