# ClaudeStatus — the manual

Everything in one place: what the app does, how it is built, how to run it from
source, how to package it, and the rules the code is written to.

`README.md` is the short version for someone who just wants to install it. This
is the long version for someone who wants to work on it.

**Contents**

1. [What it is](#1-what-it-is)
2. [Installing](#2-installing)
3. [Using it](#3-using-it)
4. [Where your data lives](#4-where-your-data-lives)
5. [Building from source](#5-building-from-source)
6. [How it is put together](#6-how-it-is-put-together)
7. [Packaging and releasing](#7-packaging-and-releasing)
8. [Rules the code follows](#8-rules-the-code-follows)
9. [Troubleshooting](#9-troubleshooting)
10. [Known gaps](#10-known-gaps)

---

## 1. What it is

A tray application that shows your Claude subscription usage without you having
to open anything. Three numbers, from the same source Claude Code's own `/usage`
command reads:

| window | what it means |
| --- | --- |
| **Session** | the rolling 5-hour limit |
| **Week** | the 7-day all-models limit |
| **Week (Fable)** | the 7-day limit for top-tier models |

The tray icon shows one of them as a number. The taskbar widget (Windows,
the default there) shows the session and weekly limits at once, and Fable if
enabled, led by the app mark so the strip is identifiable as this app.

### ⚠ The data source is unofficial

Numbers come from `GET https://api.anthropic.com/api/oauth/usage`, with an OAuth
bearer token and the `anthropic-beta: oauth-2025-04-20` header. **This is not a
published API.** It can change or vanish without notice, and its numbers can
differ slightly from claude.ai → Settings → Usage at the same instant.

This project is not affiliated with or endorsed by Anthropic. Full write-up in
[`data-source.md`](data-source.md).

---

## 2. Installing

### Windows 10/11

Download `ClaudeStatus-win-Setup.exe` from the
[latest release](https://github.com/FrancMunoz/claude-status/releases/latest) and
run it.

- Installs **per user**. No administrator rights, nothing written to
  `Program Files`, nothing in the registry except the autostart entry if you
  enable it.
- **SmartScreen may warn you the first time.** Click *More info* → *Run anyway*.
  The installer is code-signed, but a signature has to accumulate reputation
  before the warning stops; see [§7](#code-signing).
- Updates itself. See [§3](#updates).

A `ClaudeStatus-win-Portable.zip` is also published. Unzip and run — but it
**does not update itself**, and you will have to repeat this for every version.

### Uninstalling

Windows Settings → Apps → ClaudeStatus → Uninstall. The config directory is left
behind on purpose; delete it by hand if you want it gone (see
[§4](#4-where-your-data-lives)).

### macOS and Linux

Not packaged. The code for both exists behind interfaces and compiles, but
**neither has ever been executed**, so there is nothing honest to hand you yet.
You can still build and run from source ([§5](#5-building-from-source)) if you
are willing to be the first.

---

## 3. Using it

### First run

Config opens by itself, because there is no settings file yet. If Claude Code is
installed and logged in on this machine, the default source already works — close
Config and you should have numbers within a minute.

If not, the popup tells you what is missing rather than showing three dashes:

| what you see | what it means |
| --- | --- |
| *Reading your usage…* | first request in flight; nothing is wrong |
| *Not connected yet* | no credential — sign in with Claude Code, or add a token |
| *Credential rejected* | the token was refused; sign in again or replace it |
| *Asked to slow down* | rate limited; it backs off and retries by itself |
| *Cannot reach Anthropic* | network; it keeps retrying in the background |

The first two offer an **Open Config…** button. The others do not, because there
is nothing there to fix.

### The tray icon

- Shows one metric as a bare number, as large as the icon allows. The `%` and the
  three 2 px bars it used to carry were removed on 2026-09-06: at 16 px they were
  not legible enough to mean anything.
- Turns **red** at or above your threshold (default 80 %).
- Is drawn **faded** when the reading is stale, so an old number never looks
  current.
- Replaces the number with a shape when there is no number: **`!`** no
  credential, **`✕`** the shown window is exhausted, **`⊘`** the endpoint has
  never been reached. Three distinct silhouettes, because macOS renders a
  template icon as a monochrome mask and colour alone would be lost.
- Follows your taskbar's light/dark appearance. It is deliberately **not**
  themed — the taskbar is not ours to colour.

### Clicks

| | |
| --- | --- |
| **Left click** | opens the details popup; click again to dismiss |
| **Right click** | Details · Full report… · Show ▸ · Refresh · Config… · Info · Quit |

**Show ▸** switches which metric the icon displays: Session, Week, Fable, or Ring.

### The two windows

**Details** is the glance: three bars with big percentages and "resets in 2h 37m"
under each. It closes when it loses focus. **Refresh** forces a poll, subject to
a 30-second cooldown that exists so leaning on the button cannot get you rate
limited — if you hit it, the window says so rather than appearing to do nothing.

**Full report** (the *More…* button, or the context menu) is for looking things
up: every window the endpoint returned, its own severity labels, exact reset
timestamps, and the pay-as-you-go and prepaid-credit blocks — including when they
are switched off, because "off" is itself an answer. It is resizable, has a
taskbar entry, and survives being clicked away from.

### Settings

Config has four tabs:

| tab | what is on it |
| --- | --- |
| **Account** | credential source, manual token, Test button, encryption notice |
| **Display** | language, indicator (taskbar widget / tray icon, Windows only), Fable column in the widget, blend the widget into the taskbar, threshold |
| **Theme** | colour theme, font, popup transparency |
| **Behaviour** | poll interval, autostart, automatic updates, velocity alerts, fake data |

Language and theme apply **as you pick them**, before Save, so you can see what
you chose.

- **Poll interval** has a hard floor of 60 seconds. The endpoint rate-limits
  aggressively; this is not a preference.
- **Fake data** switches to a deterministic built-in provider. Useful for
  screenshots and for trying themes without burning real requests.
- **Popup transparency** is 0–100 %, default 10 %, and affects the details
  popup's background only. Never its text — a percentage you have to squint at
  defeats the point.

### Languages

English, Spanish, Catalan, German and French, following your OS by default.
Adding a sixth needs no rebuild: drop a `lang/<tag>.json` file into the config
directory. See [`translating.md`](translating.md).

### Themes

Eleven built in — light, dark, claude, ember, ocean, aurora, nebula, orchid,
sakura, solar, matcha — plus your own from a JSON file in `themes/`. A theme
declares four colours; the greys, borders and card backgrounds are derived, so a
theme author cannot produce a grey that is invisible on their own background.
Every built-in palette clears WCAG 4.5:1 on body text, and a test enforces it.
See [`theming.md`](theming.md).

### Updates

The app asks GitHub Releases for a newer version every six hours, downloads it in
the background, and installs it **the next time it starts**. It is never
restarted underneath you. When one is staged the popup offers a *Restart now*
shortcut; ignoring it is fine.

Turn it off in **Config → Behaviour**. Off means the request is not made at all,
not that the notice is hidden — this is the only host the app contacts other than
Anthropic. The feed is read anonymously, so no credential ships inside the app.

The portable zip cannot update itself and reports nothing rather than failing
forever.

---

## 4. Where your data lives

| OS | config directory |
| --- | --- |
| Windows | `%APPDATA%\ClaudeStatus` |
| macOS | `~/Library/Application Support/ClaudeStatus` |
| Linux | `$XDG_CONFIG_HOME/claudestatus` (or `~/.config/claudestatus`) |

The exact path is shown in the **Info** window and in Config's footer.

| file | what it is |
| --- | --- |
| `settings.json` | your settings. **Never contains a token** — only `HasCredential: true/false` |
| `last-snapshot.json` | the last reading, so the tray is populated instantly at startup |
| `claudestatus.log` | rolling log, 3 files kept, with a redaction filter that no log call can bypass |
| `instance.lock` | held exclusively by the running copy; released by the OS if it dies |
| `lang/*.json` | your own translations, if any |
| `themes/*.json` | your own themes, if any |

### The credential

By default **nothing is stored**. The app reads Claude Code's existing login at
poll time and keeps it only as `byte[]` for the length of the request, zeroed
afterwards.

If you paste a token manually (the advanced fallback), it is encrypted with an
**OS-bound key** and stored outside `settings.json`:

| OS | mechanism |
| --- | --- |
| Windows | DPAPI, `CurrentUser` scope, plus a per-install random 32-byte entropy file |
| macOS | Keychain, app-scoped |
| Linux | libsecret via D-Bus; if no keyring, AES-256-GCM with a `chmod 600` key file **and a visible warning in Config** |

**A copied config folder is useless on another machine or user account.** That is
by design, not a limitation. Full threat model in [`security.md`](security.md).

---

## 5. Building from source

### Prerequisites

- **.NET 10 SDK.** The exact version is pinned in `global.json`
  (`rollForward: latestFeature`, prereleases off).
- **PowerShell 7+** — only for the packaging script.
- Nothing else. There is no npm install, no code generation step.

### The three commands

```bash
dotnet build
dotnet test
dotnet format --verify-no-changes
```

All three must pass before anything is committed. `TreatWarningsAsErrors` is on
with `WarningLevel 9999`, so a warning *is* a failure.

> **Never pass `--nologo` to `dotnet test`.** Under Microsoft.Testing.Platform the
> SDK forwards unknown flags to the test application, which then prints its help
> and exits with code 5 — reported as "no tests ran" rather than as an error.

### Running it

```bash
dotnet run --project src/ClaudeStatus.App
```

Useful flags and settings while developing:

- Turn on **fake data** in Config → Behaviour to work without touching the real
  endpoint.
- A Debug build includes the Avalonia developer tools (**F12** on a focused
  window).
- Updates report *Unsupported* when running from `bin/` — Velopack knows it did
  not install this copy — so no update check happens from a dev build.

### Test layout

| project | what it covers |
| --- | --- |
| `ClaudeStatus.Core.Tests` | domain, parsing, config, security, localization, theming |
| `ClaudeStatus.App.Tests` | view models, AXAML loading, tray icon **pixels**, release config |
| `ClaudeStatus.Platform.Tests` | per-OS stores and autostart; **skip** when not on that OS |

818 tests at the time of writing, 47 of them skipped because they are for another
operating system. That skipping is normal — but if the count of *executed* tests
drops sharply, suspect a broken repo-root probe rather than celebrating.

Several tests assert on real rendered pixels — the tray icon's contrast against
light and dark taskbars, the usage bar's fill width. They boot a headless
Avalonia session with the Skia backend, because the stub drawing backend renders
nothing and every pixel assertion would pass vacuously.

---

## 6. How it is put together

```
ClaudeStatus.slnx
├─ src/
│  ├─ ClaudeStatus.Core/            no Avalonia reference. Pure .NET.
│  │   ├─ Usage/                    domain model, IUsageProvider, polling
│  │   ├─ Security/                 ISecretStore, credential handling
│  │   ├─ Config/                   IConfigStore, AppSettings
│  │   ├─ Localization/             ILocalizer, the .resx files
│  │   ├─ Theming/                  Rgb, Theme, ThemeCatalog
│  │   ├─ Update/                   IUpdateService
│  │   ├─ Logging/                  rolling file + redaction
│  │   └─ Platform/                 IAutostart, IStatusIndicator, IPlatformInfo
│  ├─ ClaudeStatus.Platform.Windows/   DPAPI, registry autostart
│  ├─ ClaudeStatus.Platform.MacOS/     Keychain, LaunchAgent
│  ├─ ClaudeStatus.Platform.Linux/     libsecret, XDG autostart
│  └─ ClaudeStatus.App/             Avalonia: views, view models, tray, DI
├─ tests/
├─ build/pack.ps1                   the release build
└─ docs/
```

### The shape of it

**Core knows nothing about Avalonia, and nothing about which OS it is on.** It
depends on interfaces that DI resolves at startup. There is no `#if` on an
operating system anywhere in Core or the UI.

The load-bearing abstractions:

| interface | responsibility |
| --- | --- |
| `IUsageProvider` | the **only** thing that knows how usage data is obtained |
| `IUsageMonitor` | polling, caching, back-off, publishing snapshots |
| `IStatusIndicator` | how usage is *shown* — today an Avalonia `TrayIcon` |
| `ISecretStore` | OS-bound encryption of a manual token |
| `IUpdateService` | check, download, apply |
| `IAutostart`, `IPlatformInfo` | the rest of the OS surface |

An official usage endpoint appearing later is a **new `IUsageProvider`** and
nothing else changes. A native macOS `NSStatusItem` is a new `IStatusIndicator`.

### Data flow

```
IUsageProvider ──▶ UsageMonitor ──▶ IObservable<UsageSnapshot>
                        │                    │
                        │                    ├─▶ TrayIconIndicator ─▶ TrayIconRenderer (a bitmap)
                        │                    ├─▶ DetailsViewModel   ─▶ DetailsWindow
                        │                    └─▶ ReportViewModel    ─▶ ReportWindow
                        └─▶ JsonSnapshotCache (so startup is instant, marked stale)
```

`TrayApplicationController` owns all of it: the monitor, the indicator, every
window's lifetime, and the settings. View models never open windows — they raise
events (`ReportRequested`, `ConfigRequested`, `UpdateRequested`) and the
controller decides, which is what lets an already-open window be reused instead
of a second one appearing.

Windows are created once and **hidden** rather than closed, so reopening is
instant and subscriptions survive.

### The tray icon is drawn, not loaded

`TrayIconRenderer` renders a 64×64 bitmap on every update: it measures the digits'
**ink bounds** (not the typographic line box, which wastes about a third of the
icon) and fits them to the whole box.
It picks its ink colour from the taskbar's appearance, and adds a halo when that
appearance cannot be determined — as on Linux, where there is no portable way to
ask.

### Patterns

MVVM with `CommunityToolkit.Mvvm`, compiled bindings (`x:DataType`) everywhere,
constructor injection via `Microsoft.Extensions.DependencyInjection`, strategy for
the provider and the indicator, observer for snapshots. **No static state.**

---

## 7. Packaging and releasing

Short version, full detail in [`releasing.md`](releasing.md).

### Build an installer locally

```pwsh
dotnet tool restore
./build/pack.ps1 -Version 0.1.0
```

Produces in `artifacts/releases/`: `ClaudeStatus-win-Setup.exe` (~28 MB), the
portable zip, the `.nupkg` the updater downloads, and the feed files
(`RELEASES`, `releases.win.json`, `assets.win.json`).

CI runs this same script with no extra arguments, so a packaging problem is
reproducible on a laptop rather than only visible in a failed workflow.

### Cutting a release

Push a Conventional Commit to `main`. That is the whole process.

| prefix | result |
| --- | --- |
| `fix:` | patch |
| `feat:` | minor |
| `feat!:` or a `BREAKING CHANGE:` footer | major |
| `docs:` `chore:` `test:` `refactor:` | no release |

semantic-release reads the commits and decides the version; each platform is then
built and signed on a runner of its own, and semantic-release tags `vX.Y.Z`,
writes the notes and attaches the installers. **Never edit a version number by
hand** — there is none in the repo to edit. MinVer derives it from the tag.

### Code signing

- **macOS** — signed with a Developer ID certificate and notarised by Apple, with
  the ticket stapled to both the `.pkg` and the `.app`. Gatekeeper *refuses* an
  unsigned installer rather than merely warning about it, so this is not optional.
  `pack.ps1` takes the identities and a `notarytool` profile, and requires all
  three or none: a partially signed build looks like it worked and is still
  refused.
- **Windows** — signed with **Azure Artifact Signing**, which issues a fresh
  certificate per request that lives 72 hours, so no key is stored anywhere and
  there is nothing to rotate. SmartScreen may still warn until the signature
  builds reputation. `pack.ps1` takes the endpoint, account and profile, and
  requires all three or none.

Windows was the less urgent of the two: it warns, where macOS refuses.

`docs/releasing.md` has the setup, the entitlements, and the CI secrets.

---

## 8. Rules the code follows

These are not style preferences. Each exists because breaking it caused, or would
cause, a specific problem.

### The credential is never the weak link

1. Never logged, printed, put in an exception message, a test fixture, a
   screenshot, a URL, or any git-tracked file.
2. Never stored in plain text, and never held in a `string` beyond the input
   control — `byte[]`, zeroed with `CryptographicOperations.ZeroMemory`.
3. Sent **only** to the official Anthropic endpoint, over HTTPS with default
   certificate validation. No proxies, no telemetry, no diagnostics upload.
4. Protected only by the OS secret store or OS-bound encryption. No hand-rolled
   crypto, no keys derived from machine names, no hardcoded salts.
5. Config is **not portable by design**.

Before every commit, grep the working tree for live `sk-ant-oat01-` /
`sk-ant-ort01-` values. And never execute an OAuth **refresh** while developing —
it rotates the user's live token out from under Claude Code.

### Localization

- **No English prose below the UI layer.** Core and the platform projects return
  *resource keys* (`DescriptionKey`, `MessageKey`, `NameKey`); only view models
  turn a key into a sentence. Writing a sentence in Core means you are in the
  wrong layer.
- All strings live in `Strings*.resx` — one key set, five files. Adding a string
  means adding it to **all five**; the tests fail otherwise.
- Keys are `Area_Thing`. A test scans the source for key-shaped literals to prove
  nothing is orphaned and nothing is missing, so **do not build a key by
  concatenation** — write it out in full, as the switches in `DetailsViewModel`
  do.
- Anything with a placeholder is composed in the view model, never with an AXAML
  `StringFormat`, because the format string is itself translated.
- `InvariantGlobalization` must stay `false`. Under it the runtime refuses every
  culture but the invariant one and no satellite set can load.

### Theming

- **Never write a literal colour into a view or a style.** Every colour is a
  `DynamicResource` named `Theme.*`. A `StaticResource` resolves once and the
  theme picker then appears to do nothing.
- A theme declares **four** colours; muted grey, card, border and warning
  surfaces are derived and must stay derived.
- Core owns the theme model and uses `Rgb`, never `Avalonia.Media.Color`.
- **The tray icon is not themed** — it follows the taskbar, which we do not
  control. The taskbar widget *is* themed: it draws on the theme's own OSD
  surface rather than on the taskbar, so the contrast problem never arises.
- The app mark leading the widget is inlined path data, not an image,
  because it has to recolour: `Theme.Primary` on the themed card,
  `Widget.Ink` when the widget blends into the taskbar. It is the one place
  the accent appears on that strip.

### Fluent controls

A `Style Selector="Button"` setting `Background` does nothing visible: Fluent's
template paints its `ContentPresenter` from named brushes and the presenter's
local value wins. Override the `Button*` / `TextControl*` / `ComboBox*` resource
keys in `ThemeApplier` instead — that reaches every control rather than the ones a
stylesheet remembered to name.

### Conventions

- .NET 10, nullable enabled, warnings as errors, central package management.
- **A new third-party package needs justifying** — maintenance, licence, size —
  and Core takes them especially reluctantly. `SnapshotSubject` and
  `RollingFileLoggerProvider` are both small hand-written types that exist
  because pulling in Rx and a logging framework could not be justified for what
  they do here.
- Compiled bindings only; styles in `/Styles`; no code-behind logic beyond wiring.
- xUnit v3 on Microsoft.Testing.Platform with AwesomeAssertions. Core targets
  ≥ 80 % coverage. Providers are tested against recorded JSON fixtures with
  secrets redacted.
- Conventional Commits. Versions come from git tags via MinVer.

---

## 9. Troubleshooting

**The icon shows `!`** — no usable credential. Open Config: either sign in with
Claude Code, or switch to a manual token.

**The icon shows `⊘`** — the endpoint has never been reached. Check the network.
The app keeps retrying on its own.

**The icon looks faded** — the reading is stale; a refresh has failed since. The
number shown is the last one that worked.

**Numbers differ from claude.ai** — expected. The endpoint is undocumented and
its windows do not update in perfect lockstep with the web UI.

**"Refreshed recently — try again in a moment"** — the 30-second manual cooldown.
It exists so the endpoint does not rate-limit you.

**The app will not start a second copy** — by design. `instance.lock` in the
config directory is held exclusively; the OS releases it if the process dies.

**A build fails with `MSB3021` / `MSB3027` (file in use)** — the running
`ClaudeStatus.exe` is holding its own output DLLs. Stop it and build again.

**A build fails with `NETSDK1151`** — something set `SelfContained` outside the
`RuntimeIdentifier` condition in `ClaudeStatus.App.csproj`. It is infectious: the
test projects reference the app and cannot reference a self-contained executable.

**Tests report far fewer than expected and nothing failed** — the repo-root probe
did not find its marker file, so the source-scanning tests skipped themselves.
Check the marker in `RepositoryRoot()`.

**Everything is English in a Release build but multilingual in tests** — the
satellite assemblies were trimmed away. `SatelliteResourceLanguages` in the app
csproj lists them explicitly for exactly this reason.

Logs are in the config directory and are redacted at the provider level, so
attaching one to a bug report is safe.

---

## 10. Known gaps

Stated plainly, because a manual that only lists what works is not much of a
manual.

- **macOS and Linux have never been executed.** Not the secret stores, not
  autostart, not the tray menu. The code compiles and is tested where it can be,
  and that is all anyone can currently claim for it.
- **No workflow has ever run.** The release pipeline is written and verified as
  far as is possible without a git remote — `semantic-release --dry-run` loads
  every plugin and stops exactly at `repositoryUrl`.
- **The update path has never been exercised.** Install one version, publish the
  next, watch it arrive — that needs a remote and two real releases.
- **The manual QA checklist** ([`qa-checklist.md`](qa-checklist.md), 79 items)
  has never been worked through. The automated suite proves the AXAML loads and
  the logic is right; it proves nothing about how the app behaves in a real tray.
- **Memory.** ~101 MB working set for the trimmed release build, down from
  ~135 MB untrimmed. The original 60 MB target is unreachable by construction — a
  bare Avalonia app with no UI at all already uses 86 MB.
- **Nothing is code-signed.** See [§7](#code-signing).

Roadmap and status: [`../PLAN.md`](../PLAN.md). Engineering diary, including why
particular decisions went the way they did:
[`CHANGELOG-dev.md`](CHANGELOG-dev.md).
