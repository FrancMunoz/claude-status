# Handoff — macOS menu bar: session badge and working dots (2026-09-25)

Prompt for a Claude Code session on the Mac. Paste it whole. Decisions below
were taken on Windows with the owner; do not reopen them.

---

Read `CLAUDE.md` and `PLAN.md` §9.5b first. This session brings the macOS menu
bar item up to what the Windows taskbar widget got in commit `8296462`
(`feat(widget): count open sessions in the badge and show three dots while a
turn runs`). Read that commit's diff to `TaskbarWidgetWindow.axaml`,
`Styles/Shared.axaml`, `TaskbarWidgetViewModel.cs` and `NativeStatusIndicator.cs`
before writing anything: the rules, the timings and the vocabulary are already
decided there and the Mac must match them, not reinterpret them.

## What to build

Two things, both drawn into the `NSStatusItem` **image**, because the item is a
16 pt image plus a plain text title and neither a rounded box nor an animation
can be done with text.

### 1. The session badge, in the image

- The image widens: the mark (or the dots, see 2) on the left, and to its right
  a rounded rectangle carrying the session count as `IndicatorText.SessionCount`
  writes it: `1/3`, working over open. No brackets inside the box.
- This **replaces** the `[1/3] ` text prefix that `NativeStatusIndicator.Compose`
  puts in front of the row today (`IndicatorText.SessionPrefix`). The row goes
  back to `5h (2:11) 56% · 7d 18%` and the count lives in the image. Keep
  `SessionPrefix` in Core only if something still uses it; otherwise remove it
  and its tests rather than leaving a dead path.
- Same rules as Windows: the badge is up whenever the session watch is on,
  `0/0` included, and gone - image back to the bare mark - when the watch is
  off (`HideSessions`). An empty list is `0/0`; off is no badge. The counts come
  from `SessionActivity.CountWorking` (busy, with `StuckAfter`) and
  `SessionActivity.CountOpen` (no end reported), recounted on every render.
- Style: the box is the same shape as the Windows one - corners of about 3 px
  at 2×, digits bold with tabular figures, 2 px of air beside the digits and
  3 above and below the ink (measure the ink, not the line box; the Windows
  comment in `Shared.axaml` explains why). Full 16 pt height for the box so the
  digits are readable at menu bar size; do not shrink it to sit over the mark's
  bottom as Windows does - at 16 pt that was judged unreadable.
- **Template image, not white-on-black.** The owner asked for a white box with
  black digits; on a light menu bar that box has no edge. The decision is:
  draw the box as opaque ink and cut the digits out of it, and keep
  `setTemplate: true`, so macOS paints it in the menu bar's own colour - white
  box / dark digits on a dark bar, dark box / light digits on a light bar, the
  same way every system item behaves. That is what "white bg, black text"
  becomes on a template image in dark mode, which is where it was asked for.
- `MacOsStatusItem.SetIcon` currently forces every image to 16 × 16 pt
  (`IconPoints`). A wider image must keep its aspect: derive the point width
  from the PNG's pixel width at the 2× scale the renderer uses (32 px tall →
  16 pt; a 64 px wide PNG → 32 pt wide). Change `SetIcon` to take or infer the
  size; do not stretch.
- Fixed width while the watch is on: the badge box must be the same width for
  `0/0` as for `9/9`, so the item does not walk along the menu bar as turns
  start and end. Size it for two digits each side (`10/12`) and centre shorter
  counts; only a third digit may widen it.

### 2. The working dots, by swapping the icon

- While `SessionActivity.CountWorking(...) > 0`, the mark in the image gives way
  to three dots pulsing in sequence - Claude's own thinking sign. The badge
  stays beside them. The moment nothing is working the mark is back. An open
  but idle session (`0/1`) shows the mark, not the dots.
- Timing matches Windows: each dot fades 0.3 → 1 → 0.3 over 0.9 s, sine eased,
  the second and third 0.3 s and 0.6 s behind the first. There is no animation
  system for an `NSStatusItem`, so this is pre-rendered frames swapped on a
  timer: **6 frames per 0.9 s cycle, one every 150 ms**, each frame the three
  dots at their opacities for that moment (pre-compute the six opacity triples
  from the same sine curve; do not eyeball them). Rasterise once per (frame,
  count) and cache; the timer only calls `SetIcon` with bytes that already exist.
- Dots are 4 px at 2× (2 pt), 2 px apart, centred in the mark's box. Dot opacity
  on a template image is honoured by macOS (template images keep alpha), so a
  dim dot is simply drawn at that alpha.
- The timer lives in `NativeStatusIndicator`, uses the injected `TimeProvider`
  (`_clock`) so tests can drive it, runs on the UI thread, **starts on the
  render that first sees a working count and stops on the first that sees none**
  - an idle app must cost nothing - and is disposed with the indicator. Rewriting
  the image 6–7 times a second is what every animated menu bar app does; if
  Activity Monitor shows it above ~1 % CPU, halve the frame rate before
  anything else.
- Nothing about the title animates. The title's vocabulary in `IndicatorText`
  is untouched except for dropping the prefix.

## Where things go

- Rendering: a new `MenuBarImageRenderer` (or extend `TrayIconRenderer`, which
  already rasterises the mark for the tray and knows the 2× rule) in
  `ClaudeStatus.App/Tray`. It takes `(frame index or null, working, open,
  watching)` and returns PNG bytes. Pure function, testable without a Mac: the
  tests decode the PNG and check the width grows with the badge, the dots
  frames differ from each other and from the mark, and the same inputs return
  the same bytes (cache).
- `NativeStatusIndicator`: chooses the image on every `Render` and on
  `ShowSessions` / `HideSessions`, owns the frame timer. Existing tests use a
  fake `INativeStatusItem` and a `FakeTimeProvider`; extend `FakeStatusItem` to
  record every icon set, and assert: no timer ticks while nothing works; ticks
  after a working session arrives; the icon sequence cycles through six
  distinct frames and repeats; a `HideSessions` or a list with no working
  session stops the ticks and restores the mark; the title no longer starts
  with `[`.
- `MacOsStatusItem.SetIcon`: point size from the bitmap, as above.
- Core: nothing new is needed beyond what `8296462` added. Do not put pixels or
  sentences in Core.

## Verify on the Mac (the reason this is a Mac session)

`docs/qa-checklist.md` §Claude Code sessions - rewrite those steps for the
image and then do them on the signed `.app`, not `dotnet run`:

- Watch on, nothing open: mark + `0/0` box, no motion, row reads exactly
  `5h (2:11) 56% · 7d 18%` with no prefix.
- One prompt in Claude Code: dots start within a second, box reads `1/1`.
- A second session working: `2/2`; one turn ends: `1/2` and the dots keep
  going; both end: `0/2`, mark back, no motion; close a terminal: `0/1`.
- Switch the watch off in Config: bare mark, box gone. On again: `0/n`.
- Both appearances: dark menu bar and light (System Settings → Appearance).
  The box edge must be visible in both. Take
  `docs/screenshots/macos-menu-bar-working.png` again for the README (⌘⇧4,
  Space, click; `screencapture` cannot) - it still shows the old `●2` form.
- Activity Monitor: CPU of ClaudeStatus while dots run, and while idle.

## Before finishing

- `dotnet build -warnaserror`, `dotnet test` (never `--nologo`),
  `dotnet format --verify-no-changes`.
- `PLAN.md` §9.5b: update the macOS item to what shipped. `docs/manual.md`
  (menu bar paragraph) and the README's macOS row and screenshot.
  `docs/CHANGELOG-dev.md`: one entry, same shape as the 2026-09-25 ones.
- One `feat(macos):` commit, pushed to `master` (the owner allows direct pushes
  this week; the ruleset squashes PRs, so a PR title would otherwise decide the
  release bump).
- Nothing in this work touches credentials; `docs/security.md` is unchanged.
  Say so in the changelog entry.
