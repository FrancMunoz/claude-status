# Releasing

How a version of ClaudeStatus gets from a commit to somebody's machine.

## The short version

**Actions → release → Run workflow**, on `master`. Releasing is a decision
someone makes, not something that happens because a branch was merged.

Merging only accumulates commits. `master` is verified continuously — `build.yml`
runs on every push and pull request — but nothing reaches a user until the release
workflow is dispatched by hand.

That is a deliberate change from running on every push to `master`. Under the
ruleset, merges are squashes, so the squashed commit message is the pull request
title — which meant a PR titled `fix: …` shipped a version the moment it landed
and one titled `docs: …` did not. Whether users got a new build came down to how
a title was worded.

The ruleset does not stand in the release's way either. It targets the branch,
and semantic-release only pushes a **tag** and creates a GitHub Release — there is
no `@semantic-release/git` plugin here, so nothing is ever committed back to
`master`.

`.github/workflows/release.yml` builds, tests, and hands over to semantic-release,
which reads the commits since the last tag and decides whether there is a release
to make:

| commit prefix | result |
| --- | --- |
| `fix:` | patch — `0.1.0` → `0.1.1` |
| `feat:` | minor — `0.1.0` → `0.2.0` |
| `feat!:` or a `BREAKING CHANGE:` footer | major — `0.1.0` → `1.0.0` |
| `docs:`, `chore:`, `test:`, `refactor:` | no release |

If there is one, it tags `vX.Y.Z`, runs `build/pack.ps1`, generates the notes from
those same commits, and attaches the installer to a GitHub Release. If there is
not — every commit since the last tag was `docs:` or `chore:` — the workflow exits
cleanly having done nothing, so dispatching it when nothing is due is harmless.

The **type** decides this and the scope is decorative: `fix(docs): …` is a `fix`
and releases a patch. A documentation change that should ship no version has to
be typed `docs:`.

**Never edit a version number by hand.** There is no version number in the
repository to edit — MinVer derives it from the tag, and the release build is
handed the version explicitly (see below).

## Building a release locally

```pwsh
./build/pack.ps1 -Version 0.1.0                      # win-x64, the default
./build/pack.ps1 -Version 0.1.0 -Runtime osx-arm64   # macOS
```

**Each platform packs on itself.** `vpk` shells out to the host's own tools - a
`.pkg` needs `pkgbuild`, a `Setup.exe` needs the Windows toolchain - so neither
can be cross-built. That is the only reason the release workflow has two jobs.

Produces, in `artifacts/releases/`:

| file | what it is |
| --- | --- |
| `ClaudeStatus-win-Setup.exe` | the Windows installer (~28 MB) |
| `ClaudeStatus-win-Portable.zip` | unzip-and-run, no installer, **no auto-update** |
| `ClaudeStatus-osx-Setup.pkg` | the macOS installer, `osx-arm64` |
| `ClaudeStatus-osx-Portable.zip` | the `.app` in a zip, **no auto-update** |
| `ClaudeStatus-<version>-full.nupkg` | the payload the updater downloads |
| `RELEASES`, `releases.win.json`, `assets.win.json` | the feed a Windows copy reads |
| `RELEASES-osx`, `releases.osx.json`, `assets.osx.json` | the feed a Mac reads |

The feeds are **per platform and not interchangeable**: a Mac reads
`releases.osx.json` and never looks at the Windows one. Attaching an installer
without its feed produces an app people can install and then never update, and
nothing about it looks like an error - `ReleaseConfigTests` asserts both sets are
listed in `.releaserc.json` for that reason.

CI runs this exact script, so a packaging problem is reproducible on a laptop
rather than only visible in a failed workflow.

## How the macOS package reaches the release

`release.yml` has two jobs, and tagging still happens exactly once.

1. **`macos`** asks semantic-release, in `--dry-run` mode, what the next version
   would be. Same commits and same config as the real run, so it reaches the same
   answer without tagging anything. If there is no release due it outputs nothing
   and every later step is skipped. Otherwise it runs `pack.ps1 -Runtime
   osx-arm64` and uploads `artifacts/releases/` as a workflow artifact.
2. **`release`** downloads that artifact into `artifacts/releases/`, then runs
   semantic-release for real. Its prepare step packs Windows into the same folder
   - `pack.ps1` creates the folder but never empties it - and the GitHub plugin
   uploads everything it finds under one tag.

The obvious alternative, a second workflow reacting to the published release,
**silently never fires**: a tag pushed with `GITHUB_TOKEN` does not trigger
another workflow, which GitHub does deliberately to prevent recursion.

## Why the version is passed in explicitly

`pack.ps1` takes `-Version` and passes `-p:Version=... -p:MinVerSkip=true` to
`dotnet publish`.

This looks redundant next to MinVer and is not. semantic-release computes the next
version and creates the tag **after** running the prepare step that builds the
artifacts. MinVer, reading tags, would see the *previous* one — so every release
would ship stamped one version behind the release it was attached to.

## What gets published, and what does not

Publish settings live in `src/ClaudeStatus.App/ClaudeStatus.App.csproj`, in a
property group conditioned on `RuntimeIdentifier` being set. That condition
matters: `SelfContained` is infectious, and setting it unconditionally makes the
two test projects that reference the app fail with `NETSDK1151`.

- **Self-contained.** Nobody installing a tray app has the .NET 10 runtime.
- **Not single-file**, though `PLAN.md` originally asked for it. Velopack already
  delivers one `Setup.exe`, so single-file buys nothing at the point of delivery —
  and it costs the thing that matters afterwards: delta updates diff the published
  folder file by file, and a single packed blob changes wholesale every release.
- **Trimmed**, `TrimMode=partial`. 50 MB published, 28 MB installer, ~101 MB
  working set measured on a real run — consistent with the trimmed figure
  predicted in the Phase 6 memory investigation.
- **No `.pdb` files.** SkiaSharp and HarfBuzz ship native symbols in their
  packages and the default rules drag them into publish: the first build was
  150 MB, 101 MB of it `libSkiaSharp.pdb` and `libHarfBuzzSharp.pdb`. A target in
  the csproj drops every `.pdb`, which is safe only because `DebugType` is
  `embedded` — our own symbols are inside the assemblies.
- **All five languages.** `SatelliteResourceLanguages` lists them explicitly.
  Without it a release build is English-only while every test stays perfectly
  multilingual, which is the worst shape a bug can have.

### Trim warnings

The publish reports IL2026/IL2072/IL2075 and does not fail on them
(`ILLinkTreatWarningsAsErrors=false`). Every one of them is in
`Avalonia.DesignerSupport` — the IDE's XAML previewer host — or the COM activator.
A shipped tray app enters neither.

**ClaudeStatus's own assemblies produce none** (checked 2026-09-05), and
`EnableTrimAnalyzer` still runs at *build* time where `TreatWarningsAsErrors`
applies, so the day our own code stops being trim-safe the build breaks. If that
list of assemblies ever grows, re-check it rather than raising the suppression.

## Updates

The app checks GitHub Releases for a newer version every six hours, downloads it
in the background, and installs it the next time it starts. It is never restarted
underneath anyone; the popup offers a "Restart now" shortcut when an update is
staged, and taking it is optional.

- Controlled by **Config → Behaviour → "Check for new versions automatically"**,
  on by default. Off means the request is not made at all, not that the notice is
  hidden — this is the only request the app makes to anything other than the
  Anthropic endpoint, so it has to be genuinely switchable.
- The feed is read **anonymously**. A token would lift GitHub's limit from 60
  requests an hour to 5000 and would also mean shipping a credential inside the
  application, which this project does not do (`docs/manual.md` §8). One check every
  six hours is nowhere near sixty.
- The **portable zip does not update itself.** Velopack reports it as not
  installed, the service reports `Unsupported`, and the UI shows nothing rather
  than a check that fails forever.

## Signing — read before the first public release

Nothing is signed. `vpk` says so on every run:

```
[WRN] No signing parameters provided, 114 file(s) will not be signed.
```

The practical consequence, and what to tell users:

- **Windows.** SmartScreen shows "Windows protected your PC" on the installer.
  Users click *More info* → *Run anyway*. This fades as the download builds
  reputation, and disappears with an Authenticode certificate (an OV certificate
  is roughly $200–400/year; EV bypasses the reputation period entirely). `vpk`
  takes `--signParams` or `--azureTrustedSignFile` when there is one.
<<<<<<< Updated upstream
- **macOS.** The `.pkg` ships unsigned and un-notarised, and `vpk` warns about
  both on every run. Gatekeeper reports it as coming from an unidentified
  developer; users right-click the `.pkg` -> **Open** -> **Open**, or run
  `xattr -dr com.apple.quarantine` on the installed app. Notarisation needs an
  Apple Developer account at $99/year, after which `vpk pack` takes
  `--signAppIdentity`, `--signInstallIdentity` and `--notaryProfile` and the
  warnings go away. This is the more urgent of the two: macOS refuses the
  installer outright where Windows only warns.
=======
- **macOS**, when it ships. Unsigned apps need a Gatekeeper bypass
  (right-click → Open, or `xattr -dr com.apple.quarantine`). Proper notarization
  needs an Apple Developer account at $99/year.
>>>>>>> Stashed changes

Both are **paid certificates**, which is why the first releases ship unsigned.
This is a deliberate, documented decision, not an oversight — but it is the first
thing to fix if the app gets an audience.

## Adding a platform

macOS is done; Linux is not. The remaining work for a platform is:

1. Add a job to `release.yml` that packs it on its own runner and uploads the
   result, and download that artifact in the `release` job. Cross-building is not
   an option - see above.
2. Teach `pack.ps1` the RID's entry-point name and icon format. Everything
   platform-specific in that script is derived from `-Runtime`.
3. Attach both the installer **and its feed** in `.releaserc.json`, and extend
   `ReleaseConfigTests` so a missing feed fails the build rather than shipping.
4. Decide the signing story first (see above).

**Intel Macs are not built.** Only `osx-arm64` is packed. Adding `osx-x64` means a
second Velopack channel, because the two would otherwise overwrite each other's
`ClaudeStatus-osx-Setup.pkg` and feed.

**Linux has never been executed.** CI builds and tests it on every push, but no
one has run the result; treat it as unverified until somebody does.
