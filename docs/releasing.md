# Releasing

How a version of ClaudeStatus gets from a commit to somebody's machine.

## The short version

Merge a Conventional Commit to `master`. That is the whole process — and it has
to be a merge: a ruleset on `master` requires a pull request whose checks pass.
The repository admin can bypass it, but the release only fires on what lands on
`master`, however it got there.

`.github/workflows/release.yml` builds, tests, and hands over to semantic-release,
which reads the commits since the last tag and decides whether there is a release
to make:

| commit prefix | result |
| --- | --- |
| `fix:` | patch â `0.1.0` â `0.1.1` |
| `feat:` | minor â `0.1.0` â `0.2.0` |
| `feat!:` or a `BREAKING CHANGE:` footer | major â `0.1.0` â `1.0.0` |
| `docs:`, `chore:`, `test:`, `refactor:` | no release |

If there is one, it tags `vX.Y.Z`, runs `build/pack.ps1`, generates the notes from
those same commits, and attaches the installer to a GitHub Release. If there is
not, the workflow exits cleanly having done nothing.

**Never edit a version number by hand.** There is no version number in the
repository to edit â MinVer derives it from the tag, and the release build is
handed the version explicitly (see below).

## Building a release locally

```pwsh
./build/pack.ps1 -Version 0.1.0
```

Produces, in `artifacts/releases/`:

| file | what it is |
| --- | --- |
| `ClaudeStatus-win-Setup.exe` | the installer people download (~28 MB) |
| `ClaudeStatus-win-Portable.zip` | unzip-and-run, no installer, **no auto-update** |
| `ClaudeStatus-<version>-full.nupkg` | the payload the updater downloads |
| `RELEASES`, `releases.win.json`, `assets.win.json` | the feed the updater reads |

CI runs this exact script with no extra arguments, so a packaging problem is
reproducible on a laptop rather than only visible in a failed workflow.

## Why the version is passed in explicitly

`pack.ps1` takes `-Version` and passes `-p:Version=... -p:MinVerSkip=true` to
`dotnet publish`.

This looks redundant next to MinVer and is not. semantic-release computes the next
version and creates the tag **after** running the prepare step that builds the
artifacts. MinVer, reading tags, would see the *previous* one â so every release
would ship stamped one version behind the release it was attached to.

## What gets published, and what does not

Publish settings live in `src/ClaudeStatus.App/ClaudeStatus.App.csproj`, in a
property group conditioned on `RuntimeIdentifier` being set. That condition
matters: `SelfContained` is infectious, and setting it unconditionally makes the
two test projects that reference the app fail with `NETSDK1151`.

- **Self-contained.** Nobody installing a tray app has the .NET 10 runtime.
- **Not single-file**, though `PLAN.md` originally asked for it. Velopack already
  delivers one `Setup.exe`, so single-file buys nothing at the point of delivery â
  and it costs the thing that matters afterwards: delta updates diff the published
  folder file by file, and a single packed blob changes wholesale every release.
- **Trimmed**, `TrimMode=partial`. 50 MB published, 28 MB installer, ~101 MB
  working set measured on a real run â consistent with the trimmed figure
  predicted in the Phase 6 memory investigation.
- **No `.pdb` files.** SkiaSharp and HarfBuzz ship native symbols in their
  packages and the default rules drag them into publish: the first build was
  150 MB, 101 MB of it `libSkiaSharp.pdb` and `libHarfBuzzSharp.pdb`. A target in
  the csproj drops every `.pdb`, which is safe only because `DebugType` is
  `embedded` â our own symbols are inside the assemblies.
- **All five languages.** `SatelliteResourceLanguages` lists them explicitly.
  Without it a release build is English-only while every test stays perfectly
  multilingual, which is the worst shape a bug can have.

### Trim warnings

The publish reports IL2026/IL2072/IL2075 and does not fail on them
(`ILLinkTreatWarningsAsErrors=false`). Every one of them is in
`Avalonia.DesignerSupport` â the IDE's XAML previewer host â or the COM activator.
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

- Controlled by **Config â Behaviour â "Check for new versions automatically"**,
  on by default. Off means the request is not made at all, not that the notice is
  hidden â this is the only request the app makes to anything other than the
  Anthropic endpoint, so it has to be genuinely switchable.
- The feed is read **anonymously**. A token would lift GitHub's limit from 60
  requests an hour to 5000 and would also mean shipping a credential inside the
  application, which this project does not do (`docs/manual.md` Â§8). One check every
  six hours is nowhere near sixty.
- The **portable zip does not update itself.** Velopack reports it as not
  installed, the service reports `Unsupported`, and the UI shows nothing rather
  than a check that fails forever.

## Signing â read before the first public release

Nothing is signed. `vpk` says so on every run:

```
[WRN] No signing parameters provided, 114 file(s) will not be signed.
```

The practical consequence, and what to tell users:

- **Windows.** SmartScreen shows "Windows protected your PC" on the installer.
  Users click *More info* â *Run anyway*. This fades as the download builds
  reputation, and disappears with an Authenticode certificate (an OV certificate
  is roughly $200â400/year; EV bypasses the reputation period entirely). `vpk`
  takes `--signParams` or `--azureTrustedSignFile` when there is one.
- **macOS**, when it ships. Unsigned apps need a Gatekeeper bypass
  (right-click â Open, or `xattr -dr com.apple.quarantine`). Proper notarization
  needs an Apple Developer account at $99/year.

Both are **paid certificates**, which is why the first releases ship unsigned.
This is a deliberate, documented decision, not an oversight â but it is the first
thing to fix if the app gets an audience.

## Adding a platform

The pack script has no Windows-specific logic. Adding macOS or Linux is:

1. Add the RID to a build matrix in `release.yml` and pass `-Runtime` through.
2. Attach the new artifacts in `.releaserc.json`.
3. Decide the signing story for that platform first (see above).

Note that **nothing in this project has ever executed on macOS or Linux**. Those
builds should be treated as unverified until somebody runs one.
