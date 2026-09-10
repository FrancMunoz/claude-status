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
can be cross-built. That is two of the three reasons the release workflow is split
into jobs; the third is the signing key, and is explained under Signing below.

Both commands above produce an **unsigned** build, which is the right default: it
is how packaging is tested, and it is the only thing a contributor without a paid
Apple account can run. To produce what a release actually ships, add the three
macOS signing parameters - all three, or none, enforced before anything is built:

```pwsh
$env:MAC_SIGN_APP_IDENTITY     = 'Developer ID Application: NAME (TEAMID)'
$env:MAC_SIGN_INSTALL_IDENTITY = 'Developer ID Installer: NAME (TEAMID)'
$env:MAC_NOTARY_PROFILE        = 'claude-status-notary'
./build/pack.ps1 -Version 0.1.0 -Runtime osx-arm64
```

Notarisation uploads to Apple and waits, so that run takes minutes rather than
seconds.

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

`release.yml` has three jobs, and tagging still happens exactly once.

1. **`decide-version`** asks semantic-release, in `--dry-run` mode, what the next
   version would be. Same commits and same config as the real run, so it reaches
   the same answer without tagging anything. If there is no release due it outputs
   an empty version and the other two jobs skip themselves.
2. **`package-macos`** imports the Developer ID certificates into a keychain it
   creates for the job and puts on the user search list - vpk cannot point
   `productbuild` at it any other way, and `codesign` reports "no identity found"
   for a keychain off the list even when given its path - runs
   `pack.ps1 -Runtime osx-arm64`, verifies the result
   with `spctl` and `stapler`, uploads `artifacts/releases/` as a workflow
   artifact, and deletes the keychain on the way out - including when the build
   failed, which is the case that matters.
3. **`release`** downloads that artifact into `artifacts/releases/`, then runs
   semantic-release for real. Its prepare step packs Windows into the same folder
   - `pack.ps1` creates the folder but never empties it - and the GitHub plugin
   uploads everything it finds under one tag.

The first two are separate jobs because of the **signing key**, not the platform.
Deciding the version means running semantic-release, which `npx` installs at run
time from a dependency tree of hundreds of packages. Anything in that tree runs
with the same access to the keychain that `codesign` has, and a stolen Developer
ID key lets someone ship malware signed as this project. So the version is decided
on a runner that holds no secrets, and handed over as a job output.

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

## Signing

**macOS is signed and notarised. Windows is not yet.** The two platforms are at
different stages, so they are described separately.

### macOS

Every release is signed with a Developer ID certificate and notarised by Apple,
then the notarisation ticket is stapled to both the `.pkg` and the `.app`. Without
that, Gatekeeper refuses the installer outright - it does not merely warn, the way
Windows does - so this is not optional for anything a user downloads.

Three things have to line up, and `pack.ps1` refuses to build unless all three are
present or all three are absent. A partial set is the dangerous case: an app signed
but a `.pkg` unsigned still gets refused, and anything signed but not notarised is
treated by Gatekeeper as if it were not signed at all. The check runs **before**
`dotnet publish`, so a mistake costs a second rather than a full build.

| parameter | what it is |
| --- | --- |
| `-MacSignAppIdentity` | the **Developer ID Application** certificate, signs the `.app` |
| `-MacSignInstallIdentity` | the **Developer ID Installer** certificate, signs the `.pkg` |
| `-MacNotaryProfile` | a `notarytool` credential profile name |

Each also reads an environment variable - `MAC_SIGN_APP_IDENTITY`,
`MAC_SIGN_INSTALL_IDENTITY`, `MAC_NOTARY_PROFILE` - which is how CI passes them
without putting them on a command line that gets echoed into a log.

An **Apple Development** certificate is not a substitute for either Developer ID
one, however much `codesign` accepts it: it carries the Code Signing EKU and signs
without complaint, but Gatekeeper admits only the Developer ID chain for software
distributed outside the App Store, so the result is refused exactly as an unsigned
build is. Note also that the two Developer ID certificates are **different
certificates**, not one reused - `productsign` rejects the Application one.

#### Entitlements

`build/macos/ClaudeStatus.entitlements` is passed to `codesign`, and its contents
are explained where it is referenced in `build/pack.ps1`. Two things about it are
worth knowing before editing it:

- Passing `--signEntitlements` **replaces** Velopack's own default file rather than
  adding to it, so removing a key here silently removes it from the signature.
- It must contain **no XML comments**. `codesign` hands entitlements to AMFI, whose
  parser is stricter than `plutil`'s and fails with
  `AMFIUnserializeXML: syntax error`. `plutil -lint` passes on a commented file, so
  only a real signing run catches it.

#### First-time setup on a machine

1. Create **Developer ID Application** and **Developer ID Installer** certificates
   for the team, choosing the **G2 Sub-CA** profile. The previous Sub-CA expires in
   February 2027 and Apple truncates any certificate issued under it to that date,
   however recently it was created.
2. Create an App Store Connect API key (Users and Access → Integrations) with
   Developer access. The `.p8` downloads exactly once.
3. Store the notary credentials under the profile name the build expects:

   ```sh
   xcrun notarytool store-credentials 'claude-status-notary' \
     --key path/to/AuthKey_XXXXXXXXXX.p8 --key-id XXXXXXXXXX --issuer <issuer-uuid>
   ```

Verify a finished build the way CI does:

```sh
spctl -a -vvv -t install artifacts/releases/ClaudeStatus-osx-Setup.pkg
xcrun stapler validate artifacts/releases/ClaudeStatus-osx-Setup.pkg
```

`source=Notarized Developer ID` is the answer you want. To check the `.app` inside
the portable zip, extract it with **`ditto -x -k`** and not `unzip`: `unzip` does
not preserve the symlinks and extended attributes inside a bundle, and its output
fails validation with `no usable signature` even when the archive is perfectly
signed.

#### CI configuration

`package-macos` reads five secrets and two variables. It checks all seven before
building and fails naming whichever is missing, because the alternative - packing
without them - produces an unsigned installer that nobody notices until a user's
Gatekeeper refuses it.

| Actions **secret** | contents |
| --- | --- |
| `MACOS_CERTIFICATE_P12` | base64 of a `.p12` holding **both** Developer ID identities |
| `MACOS_CERTIFICATE_PASSWORD` | the password set when exporting that `.p12` |
| `MACOS_NOTARY_KEY_P8` | base64 of the App Store Connect `.p8` |
| `MACOS_NOTARY_KEY_ID` | the key id, as in the `.p8` filename |
| `MACOS_NOTARY_ISSUER_ID` | the Issuer ID UUID from App Store Connect |

| Actions **variable** | contents |
| --- | --- |
| `MACOS_SIGN_APP_IDENTITY` | `Developer ID Application: NAME (TEAMID)` |
| `MACOS_SIGN_INSTALL_IDENTITY` | `Developer ID Installer: NAME (TEAMID)` |

The identities are **variables and not secrets on purpose.** They are not secret -
the certificate's common name and team id are embedded in every binary signed with
them, and `codesign -dv --verbose=4` prints both from any download. Marking them
secret would make GitHub mask them in the job log, so a mismatch between the
configured name and the imported certificate would appear as `*** not found`
instead of naming what it looked for.

Export both certificates into one `.p12` from Keychain Access → **My Certificates**
by selecting both and choosing Export. Convert without printing the value:

```sh
base64 -i certs.p12 | pbcopy
```

The private keys exist only in that `.p12` and in the keychain that made it. Apple
can reissue a certificate but has never held the key, so losing both means revoking
and starting again.

The certificates expire in **September 2031**; the API key does not expire but can
be revoked from App Store Connect. Rotating either means replacing the matching
secret and nothing else.

### Windows

Not signed. `vpk` says so on every run, and SmartScreen shows "Windows protected
your PC" on the installer - users click *More info* → *Run anyway*. This fades as
the download builds reputation.

An Authenticode certificate removes it. An OV certificate is roughly $200-400/year
and EV bypasses the reputation period entirely, but **Azure Trusted Signing** is
worth pricing first: it is a subscription an order of magnitude cheaper, and `vpk`
takes `--azureTrustedSignFile` for it as well as `--signParams` for a local
certificate. It requires identity validation, so check the current eligibility
terms before budgeting for it.

Windows is the less urgent of the two: it warns, where macOS refuses.

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
