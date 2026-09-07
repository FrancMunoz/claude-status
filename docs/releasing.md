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

`.github/workflows/release.yml` asks semantic-release what the commits since the
last tag amount to, and that answer decides whether there is a release to make:

| commit prefix | result |
| --- | --- |
| `fix:` | patch — `0.1.0` → `0.1.1` |
| `feat:` | minor — `0.1.0` → `0.2.0` |
| `feat!:` or a `BREAKING CHANGE:` footer | major — `0.1.0` → `1.0.0` |
| `docs:`, `chore:`, `test:`, `refactor:` | no release |

If there is one, the workflow builds, tests, signs and packs each platform, then
tags `vX.Y.Z`, generates the notes from those same commits and attaches the
installers to a GitHub Release. If there is not — every commit since the last tag
was `docs:` or `chore:` — every job after the first skips itself and the run
finishes green having done nothing, so dispatching it when nothing is due is
harmless.

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
can be cross-built. That is two of the reasons the release workflow is split into
jobs; the other is keeping the signing credentials off the runners that install
npm packages, and is explained under Signing below.

Both commands above produce an **unsigned** build, which is the right default: it
is how packaging is tested, and it is the only thing a contributor with neither a
paid Apple account nor an Azure subscription can run. To produce what a release
actually ships, add that platform's signing parameters - all of them, or none of
them, enforced before anything is built:

```pwsh
$env:MAC_SIGN_APP_IDENTITY     = 'Developer ID Application: NAME (TEAMID)'
$env:MAC_SIGN_INSTALL_IDENTITY = 'Developer ID Installer: NAME (TEAMID)'
$env:MAC_NOTARY_PROFILE        = 'claude-status-notary'
./build/pack.ps1 -Version 0.1.0 -Runtime osx-arm64
```

```pwsh
az login                                      # DefaultAzureCredential finds this
$env:WIN_SIGN_ENDPOINT = 'https://weu.codesigning.azure.net'
$env:WIN_SIGN_ACCOUNT  = 'the Artifact Signing account'
$env:WIN_SIGN_PROFILE  = 'the certificate profile'
./build/pack.ps1 -Version 0.1.0
```

A group belongs to one platform and the script says so rather than letting `vpk`
fail: passing the macOS parameters for `win-x64`, or the Windows ones for an `osx-`
runtime, is rejected before the build. That matters because anyone who releases
both ends up with all six exported in one shell.

Notarisation uploads to Apple and waits, so the macOS run takes minutes rather than
seconds. Signing Windows is a per-file network round trip and adds seconds.

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

## How the packages reach the release

`release.yml` has four jobs, and tagging still happens exactly once.

1. **`decide-version`** asks semantic-release, in `--dry-run` mode, what the next
   version would be. Same commits and same config as the real run, so it reaches
   the same answer without tagging anything. If there is no release due it outputs
   an empty version and every other job skips itself.
2. **`package-macos`** imports the Developer ID certificates into a keychain it
   creates for the job and puts on the user search list - vpk cannot point
   `productbuild` at it any other way, and `codesign` reports "no identity found"
   for a keychain off the list even when given its path - runs
   `pack.ps1 -Runtime osx-arm64`, verifies the result
   with `spctl` and `stapler`, uploads `artifacts/releases/` as a workflow
   artifact, and deletes the keychain on the way out - including when the build
   failed, which is the case that matters.
3. **`package-windows`** builds and tests, signs into `az login`'s federated Azure
   token, runs `pack.ps1 -Runtime win-x64`, checks the result with
   `Get-AuthenticodeSignature`, and uploads its own artifact.
4. **`release`** downloads both artifacts into `artifacts/releases/`, checks that
   every asset `.releaserc.json` lists actually arrived, and runs semantic-release
   for real to tag, write the notes and upload.

The middle two are separate jobs because of the **signing credentials**, not the
platform. semantic-release is what decides the version and what publishes, and
`npx` installs it at run time from a dependency tree of hundreds of packages.
Anything in that tree runs with the same access to those credentials that
`codesign` and `signtool` have; a stolen Developer ID key, or an Azure token good
for the rest of the job, lets someone ship malware signed as this project. So npm
runs in the first and last jobs, which hold nothing, and the signing jobs between
them run none at all.

That is also why the last job packs nothing. It used to: `release` ran
`pack.ps1` from semantic-release's prepare step, which put the npm tree and a
signing credential on one runner the moment Windows was signed too. Both platforms
now arrive already signed, and the publishing job cannot produce a binary at all.

`permissions` is declared per job for the same reason rather than once at the top.
The workflow default is read-only; only `release` can write a tag, and only
`package-windows` can mint an OIDC token.

The github plugin only *warns* about an asset path that matches nothing, so with
packing moved out of that job a half-empty release could otherwise be tagged
without anything failing. The check in step 4 reads the asset paths out of
`.releaserc.json` itself, so it cannot drift from the list that uploads them.

The obvious alternative, a second workflow reacting to the published release,
**silently never fires**: a tag pushed with `GITHUB_TOKEN` does not trigger
another workflow, which GitHub does deliberately to prevent recursion.

## Why the version is passed in explicitly

`pack.ps1` takes `-Version` and passes `-p:Version=... -p:MinVerSkip=true` to
`dotnet publish`.

This looks redundant next to MinVer and is not. Everything is packed **before**
semantic-release tags anything — it has to be, since the installers are built in
jobs that finish before the publishing one starts. MinVer, reading tags, would see
the *previous* one — so every release would ship stamped one version behind the
release it was attached to.

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

**Both platforms are signed.** They share nothing beyond that - different
authorities, different failure modes, different consequences for a user - so they
are described separately.

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

Signed with **Azure Artifact Signing** - the service Microsoft launched as Trusted
Signing and renamed - which removes the "Windows protected your PC" SmartScreen
prompt that an unsigned installer gets.

Windows is the less severe of the two: an unsigned build warns, where an unsigned
macOS build is refused outright. It was still worth doing, because a warning that
says *Unknown publisher* is the one thing a user sees before they have anything to
judge the app by.

The service issues a **fresh certificate for every signing request, valid 72
hours**, so nothing durable is stored anywhere: no `.pfx` on a machine or in a
secret, no password for one, no expiry to diarise, and nothing an attacker can take
away and use tomorrow. That is the reason to prefer it over a bought certificate as
much as the price - a Basic account is $9.99/month for 5,000 signatures, against
$200-400/year for an OV certificate whose private key then has to live somewhere.

`vpk` supports it directly. `pack.ps1` writes the three-value metadata file it
wants and passes `--azureTrustedSignFile`; the values are the account endpoint,
the account name and the certificate profile, and none of them is a credential.

#### First-time setup in Azure

The portal work, once, in this order. Steps 1-4 are on the signing service and
step 5 is what lets CI use it.

1. Register the **`Microsoft.CodeSigning`** resource provider on the subscription.
2. Create an **Artifact Signing account**. The region decides the endpoint the
   build uses - West Europe is `https://weu.codesigning.azure.net` - and an
   endpoint from the wrong region authenticates and then reports that the account
   does not exist, so note the pair together.
3. Complete **identity validation**, in the portal only; the CLI cannot do it. It
   is the long pole and the one that can fail outright, so check eligibility before
   paying for anything:
   - **Organization** needs a legal entity with three or more years of verifiable
     tax history, and takes 1-20 business days.
   - **Individual** needs a government ID and a Verified ID check against a
     billing account whose details match the certificate exactly, and is open only
     to residents of the United States and Canada.

   Assign yourself the **Artifact Signing Identity Verifier** role first, or the
   *New identity* button stays greyed out with no explanation.
4. Create a **certificate profile** of type **Public Trust** against that identity
   validation. Its name is `WINDOWS_SIGN_PROFILE`.
5. Register a Microsoft Entra **app registration** with a **federated credential**
   for this repository (issuer GitHub, `FrancMunoz/claude-status`, entity *Branch*
   → `master`), and give it the **Trusted Signing Certificate Profile Signer** role
   on the signing account. No client secret: see below.

#### CI configuration

`package-windows` reads three secrets and three variables, and checks all six
before building for the same reason the macOS job does - packing without them
succeeds and only warns.

| Actions **secret** | contents |
| --- | --- |
| `AZURE_CLIENT_ID` | the app registration's application (client) ID |
| `AZURE_TENANT_ID` | the Entra tenant ID |
| `AZURE_SUBSCRIPTION_ID` | the subscription holding the signing account |

| Actions **variable** | contents |
| --- | --- |
| `WINDOWS_SIGN_ENDPOINT` | e.g. `https://weu.codesigning.azure.net` |
| `WINDOWS_SIGN_ACCOUNT` | the Artifact Signing account name |
| `WINDOWS_SIGN_PROFILE` | the certificate profile name |

**None of these six is a signing credential, and there is no Azure secret in this
repository at all.** Authentication is OIDC: the job asks GitHub for a short-lived
token describing this repository and ref, and the federated credential trades it
for an Azure access token that expires with the job. Nothing here can be stolen and
reused, and revoking the ability to sign is a change in Azure rather than a
rotation in these settings. The three identifiers are marked secret only because
they identify the tenant; the three signing values are **variables**, exactly as
the macOS identities are, because `signtool` prints all three from any signed
download and masking them would turn a mismatch into `*** not found`.

The runner also needs the **.NET 8 runtime**, installed by a second `setup-dotnet`
step. The library that talks to Azure is a .NET 8 program and the SDK in
`global.json` neither is nor implies it; without it, signing fails after a full
build with a missing-framework error that names nothing belonging to this project.

Verify a finished build the way CI does - no Windows SDK required, and it validates
the chain against the machine's own root store, so `Valid` means a user's machine
will agree:

```pwsh
Get-AuthenticodeSignature artifacts/releases/ClaudeStatus-win-Setup.exe |
    Format-List Status, StatusMessage, SignerCertificate
```

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
