# ClaudeStatus

A tiny tray app that shows your current Claude subscription usage at a glance:
the 5-hour session window, the weekly all-models window, and the weekly Fable
window.

<p align="center">
  <img src="docs/screenshots/window-details.png" alt="The details popup showing session, week and Fable usage" width="340">
</p>

The number lives in the tray icon itself, so the answer to "do I have enough
session left to start this?" is already on screen. Click it for the detail.

> **Looking for the detail?** [`docs/manual.md`](docs/manual.md) is the full
> handbook — how it works, every setting, building, packaging, the rules the code
> follows, and troubleshooting.

---

## Install

**Windows 10/11** — download `ClaudeStatus-win-Setup.exe` from the
[latest release](https://github.com/FrancMunoz/claude-status/releases/latest) and
run it. It installs per-user, needs no administrator rights, and updates itself.

> Windows SmartScreen will warn you the first time: **More info → Run anyway**.
> The installer is not code-signed — see [why](docs/releasing.md#signing--read-before-the-first-public-release).

A `ClaudeStatus-win-Portable.zip` is also published for anyone who would rather
not install anything. It does not update itself.

**macOS and Linux** are supported in the code and are not packaged yet. The
platform layer for both exists behind interfaces, but neither has ever been
executed — see [`PLAN.md`](PLAN.md).

## Using it

The icon shows one metric — session % by default — as a bare number. It turns
**red** past your threshold (80 % by default), fades when the reading is stale, and
swaps the number for a shape when there is no number to show: `!` no credential,
`✕` exhausted, `⊘` never reached the endpoint.

### Taskbar widget — Windows only

On Windows the default indicator is not the icon but a small card in the taskbar
beside the clock, showing the session and weekly limits at once. Everything in
this section is Windows-only; macOS and Linux always use the tray icon.

It uses the same technique as [TrafficMonitor](https://github.com/zhongyang219/TrafficMonitor)
and, like it, relies on undocumented taskbar internals. If the taskbar cannot be
joined — vertical taskbar, or a Windows update that moves things — the app falls
back to the tray icon. **Config → Display** switches between the two, live, and
holds the options below.

| | |
| --- | --- |
| ![Themed card](docs/screenshots/taskbar-widget.png) | **Themed card.** Your theme's colours, outlined in its accent. |
| ![Blended into a dark taskbar](docs/screenshots/taskbar-widget-blend.png) | **Blend into the taskbar** *(default)* — no card, and the clock's own text colour. |
| ![Blended into a light taskbar](docs/screenshots/taskbar-widget-blend-light.png) | The same blend on a **light taskbar**: the ink follows the taskbar, exactly as the tray icon's does. |
| ![Themed card with the Fable column](docs/screenshots/taskbar-widget-fable.png) | **Show Fable** adds the weekly Fable limit as a third column. Off by default — most plans hit the session or weekly limit first. |

The widget is themed, so the card follows whichever theme you pick:

<p align="center">
  <img src="docs/screenshots/themes/widget-claude.png" alt="Taskbar widget in the Claude theme" width="200">
  <img src="docs/screenshots/themes/widget-nebula.png" alt="Taskbar widget in the Nebula theme" width="200">
  <img src="docs/screenshots/themes/widget-matcha.png" alt="Taskbar widget in the Matcha theme" width="200">
</p>

Hovering the widget opens a card with all three limits and their reset times,
whatever the widget itself is showing.

- **Left click** — the details popup. Click again to dismiss it.
- **Right click** — Details, Full report…, Show ▸ (switch metric), Refresh,
  Config…, Info, Quit.

| | |
| --- | --- |
| <img src="docs/screenshots/report/report-en.png" alt="The full report window" width="330"> | <img src="docs/screenshots/window-config.png" alt="The configuration window" width="300"> |
| **Full report** — every window the endpoint returned, its own severity labels, exact reset timestamps, and the spend and credit blocks. | **Config** — credential, language, theme, threshold, poll interval, autostart. |

### Languages

English, Spanish, Catalan, German and French, following your OS by default. A
sixth needs no rebuild — drop a translated JSON file into the config directory.
See [`docs/translating.md`](docs/translating.md).

### Themes

Eleven built in, plus your own from a file. **Claude** is the default; *Follow the
system* is one click away in the picker. Every one of them clears WCAG 4.5:1 on
body text; a palette that fails is not shipped.

<p align="center">
  <img src="docs/screenshots/themes/claude.png" alt="Claude theme" width="210">
  <img src="docs/screenshots/themes/nebula.png" alt="Nebula theme" width="210">
  <img src="docs/screenshots/themes/matcha.png" alt="Matcha theme" width="210">
</p>

See [`docs/theming.md`](docs/theming.md).

## ⚠ Unofficial data source

Usage numbers come from `GET https://api.anthropic.com/api/oauth/usage` — the
same undocumented endpoint Claude Code's own `/usage` command calls. It is
**not a published API**. It can change or disappear without notice, and the
numbers may differ slightly from what claude.ai Settings → Usage shows at the
same instant.

This project is not affiliated with or endorsed by Anthropic.

See [`docs/data-source.md`](docs/data-source.md) for the full write-up.

## Security

ClaudeStatus is built so the credential is never the weak link:

- By default it **stores no token at all** — it reads Claude Code's existing
  login at poll time and keeps nothing.
- If you do paste a token manually (the "advanced" fallback), it is encrypted
  with an **OS-bound key**: DPAPI on Windows, the Keychain on macOS, libsecret
  on Linux. A copied config folder is useless on another machine or user account.
- The only host it ever talks to about your usage is `api.anthropic.com`, over
  HTTPS with default certificate validation. No telemetry, no diagnostics upload,
  no proxies.
- The one other request it makes is the update check against GitHub Releases,
  which sends nothing about you and can be switched off in Config → Behaviour.

Details and threat model: [`docs/security.md`](docs/security.md).

## Building from source

Requires the .NET 10 SDK.

```bash
dotnet build
dotnet test          # note: do NOT pass --nologo, it is forwarded to the test app
dotnet format --verify-no-changes
dotnet run --project src/ClaudeStatus.App
```

To build the installer:

```pwsh
dotnet tool restore
./build/pack.ps1 -Version 0.1.0
```

See [`docs/releasing.md`](docs/releasing.md).

## Contributing

Commits follow [Conventional Commits](https://www.conventionalcommits.org/) —
they are what decides the next version number, so the prefix matters. Versions
are derived from git tags by MinVer; never edit a version by hand.

[`docs/manual.md`](docs/manual.md) is the full handbook: architecture, the rules
the code follows, packaging, and troubleshooting. Read §8 before opening a PR.

## Licence

[MIT](LICENSE). Built with [Avalonia UI](https://avaloniaui.net) and packaged
with [Velopack](https://velopack.io).
