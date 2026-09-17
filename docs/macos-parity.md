# macOS parity: session features

Handoff from the Windows machine (2026-09-17). Work on `feat/widget-working-badge`,
the single working branch. `PLAN.md` is git-ignored, so the macOS plan items it
holds on the Windows machine are restated here: copy them into your local
`PLAN.md` as a new section.

Follow `CLAUDE.md` §0 before writing any code: call `get_avalonia_expert_rules`,
and use the Avalonia MCP tools for every Avalonia question.

## Context: what already works everywhere

- Core: `SessionRegistry`, `SessionActivity` (who counts as working, with a 2 h
  `StuckAfter`), `ClaudeCodeHooks` (hooks for `SessionStart`, `UserPromptSubmit`,
  `Stop`, `Notification`/`idle_prompt` only, `SessionEnd`), `SessionSpool`.
- App: `HookEntryPoint` (the same binary, run by Claude Code as a hook, writes
  one spool file), `SessionWatcher`, the session list and switches in the
  details popup, the pace warning card (`UsageNoticeCard`), which
  `NativeStatusIndicator` already shows.
- Windows only today: the working badge (`TaskbarWidgetWindow`), OS notifications
  (`WindowsNotifier`: toast, or a shell balloon), click a notification → focus the
  session's terminal (`WindowsTerminalFocus`), and "no notification while that
  terminal has focus" (`ITerminalFocus.IsForeground`).
- On macOS, `PlatformServices` gives `NullNotifier` and `NullTerminalFocus`, so a
  session notice falls back to the app's own card, and clicking it does nothing.

## Step 0: check before you build (report back, change nothing yet)

Run the app as the built `.app`, not `dotnet run`, and confirm:
1. `~/.claude/settings.json` gets our five hooks (marker `--claude-status-hook`),
   the `command` is the executable inside the bundle, and they disappear on quit.
2. A real Claude Code turn writes a spool file and shows in the details popup as
   working → waiting. `Console.OpenStandardInput` reads the payload in the hook
   process.
3. Esc mid-turn: the session goes back to waiting about a minute later
   (`idle_prompt`).
4. The session card appears near the menu bar when a turn ends.
5. With no credential (sign Claude Code out, or point Config at a manual token and
   forget it) the menu bar reads `! No credential`, in the alert colour, not
   `5h !`. Also check `— No data` at launch and `⊘ Offline` with the network off.
   This landed in `c5f3798` and has never been seen on a Mac.
Anything broken here is fixed first, as its own `fix:` commit.

## Step 1: working indicator in the menu bar (`NativeStatusIndicator`)

The menu bar is text, so no badge: prefix the row with a glyph and the count
while any session is working, e.g. `●2 5h (2:11) 56% · 7d 18%`, with the count
shown from 1, like the Windows badge. Rules:
- The wording lives in `IndicatorText` (Core), as the rest of the row does, and
  gets tests. The count comes from `SessionActivity.CountWorking`.
- Recompute on `ShowSessions` *and* on every `Render`, so a session killed
  mid-turn drops off after `StuckAfter` without a new event (the widget view
  model does the same).
- No animation in this step. If you think a pulse is worth an `NSStatusItem`
  title rewritten on a timer, ask first.
- Check it is legible in light and dark menu bars. No count beside the
  no-reading words (`! No credential`, `⊘ Offline`, `— No data`, from
  `IndicatorText.Absence`), and decide whether a count beside an exhausted `x`
  reads clearly.

## Step 2: real notifications (`MacNotifier : INotifier`)

- `UNUserNotificationCenter`: request authorisation once, post with the session
  id in `userInfo`, and raise `Activated(tag)` from the delegate's
  `didReceiveNotificationResponse`. The controller already marshals `Activated`
  to the UI thread.
- The delegate has to be set early enough to receive a click that launched or
  woke the app. Find out where that is for an Avalonia app, and don't guess.
- `Notify` returns false when there is no bundle id (`dotnet run`), when
  permission was denied, or when the post fails. The controller then shows the
  card, so that path must keep working.
- Same `tag` replaces a session's earlier notification.
- P/Invoke through the existing `Interop` folder's style. No new NuGet package
  without asking (CLAUDE.md §8). Say whether this needs anything in
  `build/macos/Info.plist.template` or the entitlements.

## Step 3: focus the session's terminal (`MacTerminalFocus : ITerminalFocus`)

- `Capture()` runs **in the hook process**, which is short-lived and has no UI.
  Walk the parent chain (`proc_pidinfo` with `PROC_PIDTBSDINFO` for `pbi_ppid`
  and start time) up to the first ancestor whose `proc_pidpath` is inside a
  `.app/Contents/MacOS/` bundle (Terminal, iTerm2, VS Code, Ghostty, …). Stop at
  launchd (pid 1). Prefer that over loading AppKit in the hook process.
- `SessionOrigin.Window` has no macOS meaning, and `SessionSpool` currently
  drops any origin with `Window == 0` (`SessionSpool.cs`, the validation near
  `ProcessId > 0 && Window != 0`). Choose between a documented non-zero
  placeholder and making the rule depend on `Precision` (`Process` needs no
  window). I lean towards the second. Either way it needs a test, and the
  Windows behaviour must not change.
- `TryFocus`: re-check pid *and* start time (pid reuse), then
  `NSRunningApplication` `activateWithOptions:`. Activate the app only: picking
  the right window needs Accessibility permission, which is out of scope.
- `IsForeground`: `NSWorkspace.frontmostApplication` is the recorded pid (with
  the start time checked).
- Wire both into `PlatformServices.CreateNotifier` / `CreateTerminalFocus` for
  macOS.

## Rules for all of it

- Stay on `feat/widget-working-badge` (no new branches); at least one commit per
  step, Conventional Commits (`feat(mac): …`, `fix(mac): …`).
  Restate the plan for each step and wait for my OK before touching more than
  one file.
- Platform code stays in `ClaudeStatus.Platform.MacOS` behind the Core
  interfaces, with no `#if` in UI or Core. Platform tests are skipped when not on
  macOS.
- No English below the view models. Any new user-facing string goes into all
  five `Strings*.resx`.
- Before each commit: `dotnet build -warnaserror`, `dotnet test` (never with
  `--nologo`), `dotnet format --verify-no-changes`. The Windows build must still
  compile: CI runs all three OSes.
- Update `docs/CHANGELOG-dev.md`, `docs/qa-checklist.md` (a macOS sessions
  section), `docs/manual.md`, and the README's "Claude Code sessions" section,
  which currently says macOS gets the card only.
- Linux (`org.freedesktop.Notifications`, focusing) stays out of scope. Just
  note what you learn.
