# Manual QA checklist

The automated suite proves the AXAML loads, the bindings resolve, and the logic
is right. It proves **nothing** about how the app looks and behaves in a real
tray, which is the entire product. Work through this on each OS before tagging a
release.

Record the date, OS version and app version next to each run.

---

## 0. Before you start

- [ ] Build in **Release**. Debug pulls in the developer tools and renders differently.
- [ ] Start from a clean profile: delete the config directory shown in the Info
      window, so you exercise the first-run path.
- [ ] Have Claude Code installed and logged in, and know your real usage numbers
      from `/usage` so you can compare.

## 1. First run

- [ ] The Config window opens by itself on a machine with no settings file.
- [ ] The tray icon appears immediately, before any data arrives.
- [ ] With no data yet the icon shows `--` in grey rather than looking broken.
- [ ] Within about a minute the icon shows a real number.
- [ ] The number matches Claude Code's `/usage` within a point or two.
      *Small differences are expected and documented; a large one is a bug.*

## 2. The tray icon

- [ ] Left click opens the details window; a second left click closes it again.
- [ ] Clicking the icon while the popup is open closes it and does **not** reopen
      it (the popup hides on losing focus, so this is the case that breaks).
- [ ] At 100 % the number becomes a cross, and the bars still show.
- [ ] Starting with no network and no cached reading shows the struck-through
      circle, not a dash (a dash means "still loading").
- [ ] The three bars under the number track session, week and Fable, and match
      the popup's bar colours after a theme change.
- [ ] With the network off, the icon keeps the last reading but draws it faded,
      and the tooltip says the endpoint is unreachable.
- [ ] Right click opens the context menu.
      *On macOS a single click opens the menu — this is the platform's behaviour,
      not a bug. Details is the first menu item for that reason.*
- [ ] **Show ▸** lists four modes with a radio tick on the current one.
- [ ] Switching mode changes the icon immediately.
- [ ] The chosen mode survives a restart.
- [ ] Hovering shows a tooltip with all three metrics.
- [ ] The tooltip says "stale" after the network has been off for a few minutes.

## 3. The threshold

- [ ] Set the threshold below your current usage. The icon turns **red** at once.
- [ ] Set it above. The icon returns to normal.
- [ ] The red is clearly visible against **both** a light and a dark tray
      background. Change the system theme and look again.
- [ ] **The NORMAL (non-red) icon is visible on a light taskbar.**
      *Was broken; fixed 2026-09-04 by reading the taskbar theme. Switch Windows
      between light and dark and confirm the ink flips within one poll.*
- [ ] The Ring at 100 % is a full circle, not a dot.
      *Was broken; ArcTo from a point to itself is degenerate.*
- [ ] **Linux:** the icon is readable on whatever colour the panel happens to be.
      *There is no theme signal there, so the glyph is drawn with a contrasting
      outline instead. Check on both a light and a dark panel.*
- [ ] **macOS only:** in normal state the icon follows the menu bar's appearance
      (light on dark, dark on light). In the red state it stays red.
      *This is the `IsTemplateIcon` switch. If red comes out monochrome, that
      logic is inverted.*

## 4. The details window

- [ ] Opens near the tray, not in the middle of the screen.
- [ ] Shows three bars with percentages and "resets in …" countdowns.
- [ ] Countdowns are plausible and count **down** if you leave it open.
- [ ] A plan with no Fable window shows a dash, not `0 %`.
- [ ] Closes when it loses focus.
- [ ] Reopening is instant and shows current values.
- [ ] **Refresh** updates the numbers.
- [ ] Pressing Refresh twice quickly explains the cooldown rather than doing
      nothing silently.
- [ ] **Session rows** (Windows and macOS, session watch on, a prompt sent from
      a terminal, then another app in front):
      *Windows: passed 2026-09-17. macOS: not yet recorded.*
  - [ ] Hovering a running session's row shows a fill, a hand cursor and the
        "Show its terminal" tooltip.
  - [ ] Clicking it brings that terminal forward and the popup closes. The log
        says `Session row clicked for … focused its window`.
  - [ ] Clicking the row's switch only toggles notifications; it never focuses.
  - [ ] A finished session's row looks unchanged (not greyed) and does nothing.
  - [ ] **Linux:** no row reacts to hover or clicks.

## 5. The Config window

- [ ] Every control is readable and nothing is clipped at the default size.
- [ ] Resizing smaller produces a scrollbar rather than cut-off content.
- [ ] The threshold and interval sliders update their labels as you drag.
- [ ] The poll interval cannot be set below 60 s.
- [ ] The encryption notice is present and names the mechanism for this OS.
- [ ] The credential box masks what you type.
- [ ] Pasting a **refresh** token (`sk-ant-ort…`) is rejected with a message
      naming the mistake.
- [ ] Pasting nonsense is rejected without the app crashing.
- [ ] **Test credential** reports success against a good login.
- [ ] **Test credential** on a machine with no login reports "no credential",
      not a generic error.
- [ ] Settings survive a restart.

## 6. Autostart

- [ ] Ticking the box and logging out and back in starts the app.
- [ ] Unticking it stops that happening.
- [ ] The entry is per-user and appears in the expected place:
  - Windows: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
  - macOS: `~/Library/LaunchAgents/com.zeroworks.claudestatus.plist`
  - Linux: `~/.config/autostart/claudestatus.desktop`
- [ ] Ticking the box does **not** launch a second copy immediately.
- [ ] Installing to a path with a space in it still autostarts correctly.
      *This is the quoting bug the code guards against; worth actually trying.*

## 6b. Single instance

- [ ] Launch the app twice by hand. Only one tray icon appears, and the second
      process exits on its own.
- [ ] Kill the app from Task Manager / `kill -9`, then launch it again. It starts
      normally. *A lock left behind by a crash would make the app permanently
      unlaunchable, which is far worse than a duplicate icon.*
- [ ] With autostart enabled, log out and back in while the app is already
      running. Still only one icon.
- [ ] Two different users logged into the same machine each get their own
      instance.

## 6c. Quitting

With session watch on, so there are hooks to take out:

- [ ] **Quit** from the menu: the process exits and `~/.claude/settings.json` holds
      none of our hooks (marker `--claude-status-hook`).
- [ ] Open Config (or Report, or Info) and close it, then quit from the menu:
      the same.
- [ ] The same, but quit from outside the app instead - macOS:
      `osascript -e 'tell application id "com.zeroworks.claudestatus" to quit'`
      returns without error and the process is gone within a few seconds.
- [ ] Log out with the app running and a hidden Config window: the logout is
      **not** interrupted by ClaudeStatus (macOS and Windows).
- [ ] Relaunch after any of the above: exactly one menu bar item or tray icon.

## 7. Failure behaviour

- [ ] Turn the network off. The app keeps running and shows stale values.
- [ ] Turn it back on. Values recover without a restart.
- [ ] **Offline start:** with the network off, quit and relaunch. The tray shows
      the cached numbers immediately, marked stale — not a blank icon.
- [ ] Delete `last-snapshot.json` and relaunch offline. The icon shows the
      no-data state rather than crashing.
- [ ] Leave the app off for more than two days, then start it offline. The cache
      is ignored as too old rather than showing a stale number as if it were
      current.
- [ ] Quit Claude Code and wait for the token to expire (about an hour). The app
      says the login needs refreshing rather than showing a rate-limit message.
- [ ] **Credential alert:** with no credential at all, the icon shows `!` in red
      and a **left click opens Config directly**, not the details window.
- [ ] The tooltip in that state says what to do.
- [ ] A pure network failure does **not** show the `!` glyph — there is nothing
      for the user to act on, so it must not nag.
- [ ] Nothing ever shows an unhandled exception dialog.

## 8. Appearance

- [ ] Light theme: all text readable, warning banners legible.
- [ ] Dark theme: same.
- [ ] HiDPI (200 % scaling): the tray icon is sharp, not blurry or clipped.
- [ ] Mixed-DPI multi-monitor: drag the details window between screens; it stays
      sharp and correctly sized.
- [ ] 100 % usage renders inside the icon without being cut off.

## 9. Resource use

*Targets from `PLAN.md` Phase 6.*

- [ ] Idle memory under 60 MB after ten minutes.
- [ ] CPU at 0 % between polls.
- [ ] Memory does not grow over an hour.
      *The tray icon allocates a new bitmap on every poll; a steady climb here
      means one is not being disposed.*

## 10. Security spot checks

*The full list is `docs/security.md` §6. These are the ones that need a person.*

- [ ] The log file in the config directory contains no `sk-ant-` string.
- [ ] The settings file contains no token, only `hasCredential`.
- [ ] `last-snapshot.json` contains only percentages and timestamps.
- [ ] Copy the whole config directory to another user account on the same
      machine. The app there cannot read the credential.
- [ ] **Linux without a keyring:** the Config window shows the weak-store warning.
- [ ] **Linux with a keyring:** it does not.

---

## Per-OS notes

### Windows
- Test on both Windows 10 and 11 if you can; the notification area differs.
- Check the icon in the overflow ("hidden icons") area too, not just when pinned.
- **Session notification click** (with session watch on, a prompt sent, then
  switch to another app and wait for the "waiting for you" toast):
  - [ ] Windows Terminal: clicking the toast brings that terminal window forward.
    With two WT windows, the one the prompt was typed into comes forward.
  - [ ] VS Code integrated terminal: the right VS Code window comes forward.
  - [ ] Classic console (`conhost`): the console window comes forward.
  - [ ] The terminal minimised: it is restored, not just flashed.
  - [ ] The terminal closed before clicking: nothing is focused, nothing crashes.
  - [ ] A session that started before this build: the details window opens instead.
  - [ ] Installed (Start has "ClaudeStatus"): the toast shows and **no** extra
    icon appears in the tray or its overflow, ever.
  - [ ] Not installed (unzipped copy, or `dotnet run` on a machine without the
    install): the toast still shows; an icon appears only while it is up and is
    gone once it times out or is clicked.
  - [ ] Clicking the entry in the Action Centre after the toast has gone: note
    what happens (the shell may not report it at all).

### macOS
- Test on both Intel and Apple Silicon if available.
- First run prompts for Keychain access when reading Claude Code's login. That
  prompt is correct and must not be suppressed. Check that declining it produces
  "no credential", not a crash.
- **Claude Code sessions** (run the built `.app`, not `dotnet run`; session watch on):
  - [ ] Launch: `~/.claude/settings.json` gains five hooks whose `command` is the
    executable inside the bundle. Quit from the menu: they are gone.
  - [ ] Session watch on and nothing open: the menu bar row starts with `[0/0] `.
    Switch the watch off in Config: the prefix goes and the row reads exactly
    as before. Switch it back on.
  - [ ] A prompt in Claude Code: the row reads `[1/1] ` within a second or two,
    and the details popup lists the session as working.
  - [ ] A second session working at once: `[2/2] `. One turn ends: `[1/2] `.
    Both end: `[0/2] `. Close one terminal: `[0/1] `.
  - [ ] The prefix is legible on a light menu bar and on a dark one (switch
    appearance, or use a light and a dark wallpaper).
  - [ ] Signed out of Claude Code while a turn runs: `! No credential` in red,
    with no count beside it.
  - [ ] First launch of a signed build: macOS asks whether ClaudeStatus may send
    notifications. The log reads `Launch already finished: False` for the
    notification delegate.
  - [ ] Allowed: when a turn ends a notification appears, and **no** card. The log
    says `MacNotifier accepted it`.
  - [ ] Two turns of the same session end: Notification Center holds one
    notification for it, not two.
  - [ ] Clicking the notification brings the session's terminal app forward, in
    each of: Terminal, iTerm2, VS Code's integrated terminal, Ghostty. The log
    says `focused its window (Process)`.
  - [ ] The terminal app in front when a turn ends: no notification, and the log
    says `not notified, its terminal has focus`.
  - [ ] The terminal quit before clicking: nothing is focused, nothing crashes.
  - [ ] A session over ssh, or in tmux started outside a terminal app: the click
    opens the details popup.
  - [ ] With the popup open (the app in front), a turn ending still shows a banner.
  - [ ] Denied, or turned off later in System Settings → Notifications: the
    "waiting for you" card appears under the menu bar instead, and the log says
    `MacNotifier refused it`.
  - [ ] Under `dotnet run` (no bundle): no crash, the card appears.

### Linux
- Test at least KDE and GNOME.
- On bare GNOME, confirm the tray warning appears and the app still runs.
- With the AppIndicator extension installed, confirm the icon actually shows.
- Test both with and without `gnome-keyring` running.
