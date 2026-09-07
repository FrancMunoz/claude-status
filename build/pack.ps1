<#
.SYNOPSIS
    Publishes ClaudeStatus and builds the Velopack installer.

.DESCRIPTION
    The whole of the release build, in one script that CI and a developer run
    identically. Nothing here is GitHub-specific: `./build/pack.ps1 -Version 1.2.3`
    on a laptop produces the same artifacts the release workflow uploads, which is
    the only way a packaging problem gets found before a tag exists rather than
    after.

.PARAMETER Version
    The version to stamp, without a leading "v".

    Passed explicitly rather than left to MinVer because semantic-release computes
    the next version and creates the tag AFTER running this script. MinVer would
    read the previous tag and every release would ship stamped one version behind.

.PARAMETER Runtime
    The .NET RID to build. win-x64 and osx-arm64 are released today.

    Everything that differs between them is derived from this one value - the
    entry point's file name, the icon format, and whether an Info.plist is needed
    - so a caller picks a platform rather than a set of matching flags that could
    disagree with each other.

.PARAMETER OutputDir
    Where the installer and packages are written.

.PARAMETER MacSignAppIdentity
    The "Developer ID Application" certificate that signs the .app bundle.

    Defaults to $env:MAC_SIGN_APP_IDENTITY so that CI passes it as an environment
    variable rather than on a command line, where it would be echoed into the
    build log.

    An "Apple Development" certificate is NOT a substitute, even though it carries
    the Code Signing EKU and codesign accepts it without complaint. Gatekeeper
    admits only the Developer ID chain for software distributed outside the App
    Store, so signing with it produces the same "unidentified developer" refusal as
    not signing at all - just later, and after notarisation has been rejected.

.PARAMETER MacSignInstallIdentity
    The "Developer ID Installer" certificate that signs the .pkg.

    A different certificate from the one above, not the same one reused: pkgbuild
    and codesign trust different EKUs, and the Application certificate is rejected
    for an installer package.

.PARAMETER MacNotaryProfile
    The name of a notarytool credential profile, stored beforehand with
    `xcrun notarytool store-credentials`.

    Signing without notarising is not a half-measure that gets a half-result. Since
    macOS 10.15 Gatekeeper refuses a signed-but-unnotarised app much as it refuses
    an unsigned one, so the three parameters are required as a set - see the check
    below.

.EXAMPLE
    ./build/pack.ps1 -Version 0.1.0

.EXAMPLE
    ./build/pack.ps1 -Version 0.1.0 -Runtime osx-arm64 `
        -MacSignAppIdentity 'Developer ID Application: Example (TEAMID1234)' `
        -MacSignInstallIdentity 'Developer ID Installer: Example (TEAMID1234)' `
        -MacNotaryProfile 'claude-status-notary'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string] $Version,

    [string] $Runtime = 'win-x64',

    [string] $OutputDir = 'artifacts/releases',

    [string] $MacSignAppIdentity = $env:MAC_SIGN_APP_IDENTITY,

    [string] $MacSignInstallIdentity = $env:MAC_SIGN_INSTALL_IDENTITY,

    [string] $MacNotaryProfile = $env:MAC_NOTARY_PROFILE
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    $publishDir = Join-Path $repoRoot "artifacts/publish/$Runtime"
    $releaseDir = Join-Path $repoRoot $OutputDir

    # A stale publish folder is worse than no publish folder: trimming leaves
    # whatever the previous run produced, and a removed file stays behind and
    # ships.
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null

    # Not $isMacOS, however much it reads better. PowerShell variable names are
    # case-insensitive, so that is $IsMacOS - an automatic, read-only, AllScope
    # variable in PowerShell 6+ - and assigning to it throws "Cannot overwrite
    # variable IsMacOS because it is read-only or constant". It fails on every
    # platform, not only macOS: the automatic variable exists everywhere and is
    # merely $false off a Mac.
    #
    # It also means the question this asks is not the one the name implied. We
    # want the runtime being *built for*, which on CI is the same machine only by
    # coincidence - the whole point of -Runtime is that they can differ.
    $packForMac = $Runtime.StartsWith('osx-')

    # All three or none of the three, decided before anything is built.
    #
    # Every partial combination produces something that looks like it worked and is
    # not shippable: the app signed but the .pkg not, so the installer is still
    # refused; or both signed but nothing notarised, which Gatekeeper treats as
    # unsigned. Nothing is required, though - an unsigned build is the normal way to
    # test packaging, and a contributor without a paid Apple account has to be able
    # to run this script.
    #
    # Checked here rather than beside the vpk arguments it feeds, because the publish
    # step sits between the two and takes minutes. A mistyped parameter should cost a
    # second, not a full build that fails at the very end.
    $signing = [ordered] @{
        MacSignAppIdentity     = $MacSignAppIdentity
        MacSignInstallIdentity = $MacSignInstallIdentity
        MacNotaryProfile       = $MacNotaryProfile
    }
    $missing = @($signing.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object { $_.Key })
    $signPackage = $missing.Count -eq 0

    if ($missing.Count -gt 0 -and $missing.Count -lt $signing.Count) {
        throw "macOS signing needs all of $($signing.Keys -join ', '), or none of them. Missing: $($missing -join ', ')."
    }

    # Signing arguments are macOS-only; vpk rejects them on a Windows build rather
    # than ignoring them, so a shell that exports them globally would break win-x64.
    if ($signPackage -and -not $packForMac) {
        throw "macOS signing parameters were supplied for runtime '$Runtime'. They apply only to an osx- runtime."
    }

    # Notarisation requires the hardened runtime, whose defaults kill a .NET app on
    # startup, so the bundle is signed with an explicit entitlements file. Its five
    # keys are Velopack's own vendor/Velopack.entitlements, copied deliberately:
    # passing --signEntitlements REPLACES that fallback rather than adding to it, so
    # dropping a key here silently removes it from the signature.
    #
    #   allow-jit + allow-unsigned-executable-memory
    #       Both, however redundant they read. RyuJIT (libclrjit.dylib) emits code at
    #       runtime; the first grants the MAP_JIT mapping it asks for, the second
    #       covers paths in libcoreclr.dylib that write executable pages without it.
    #       With only the first the process still dies, and the crash report names
    #       CODESIGNING rather than the JIT.
    #
    #   disable-library-validation
    #       Avalonia's renderer is three NuGet-supplied natives - libAvaloniaNative,
    #       libSkiaSharp, libHarfBuzzSharp - signed by someone who is not this team.
    #       Validation admits only same-team or Apple libraries, so dyld refuses all
    #       three and no window ever appears.
    #
    #   allow-dyld-environment-variables + automation.apple-events
    #       For the update path, which is the worst place to lose an entitlement: a
    #       failed update is silent by nature - the running app carries on and simply
    #       never moves version, which looks identical to no release being out.
    #
    # That file must contain NO XML comments, however much the rest of this
    # repository argues otherwise. codesign hands entitlements to AMFI, whose parser
    # is stricter than plutil's and rejects them outright:
    #
    #   Failed to parse entitlements: AMFIUnserializeXML: syntax error near line 11
    #
    # `plutil -lint` passes on a commented file, so the only thing that catches it is
    # a real signing run. Hence the explanation living here instead.
    $entitlements = Join-Path $repoRoot 'build/macos/ClaudeStatus.entitlements'
    if ($signPackage -and -not (Test-Path $entitlements)) {
        throw "Entitlements file not found at $entitlements."
    }

    if ($packForMac -and -not $signPackage) {
        Write-Host "==> Unsigned macOS build; Gatekeeper will refuse it on another machine." -ForegroundColor Yellow
    }

    Write-Host "==> Publishing $Runtime at $Version" -ForegroundColor Cyan
    dotnet publish src/ClaudeStatus.App/ClaudeStatus.App.csproj `
        --configuration Release `
        --runtime $Runtime `
        --output $publishDir `
        -p:Version=$Version `
        -p:MinVerSkip=true
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    # The entry point keeps the platform's own convention: a .exe on Windows, an
    # extensionless Mach-O on macOS. Naming the wrong one fails inside vpk with a
    # message about a missing file rather than about the platform.
    $mainExe = if ($packForMac) { 'ClaudeStatus' } else { 'ClaudeStatus.exe' }

    # Each platform reads only its own icon container.
    $icon = if ($packForMac) {
        'src/ClaudeStatus.App/Assets/claude-mark.icns'
    } else {
        'src/ClaudeStatus.App/Assets/claude-mark.ico'
    }

    $extraArgs = @()

    if ($packForMac) {
        # The bundle's Info.plist is generated rather than shipped, because two of
        # its values are the version and vpk has no way to substitute them into a
        # file it is handed. Everything else in it is fixed - see the template for
        # why LSUIElement is the reason it exists at all.
        #
        # CFBundleShortVersionString has to be plain x.y.z: Apple rejects a
        # prerelease suffix there, while CFBundleVersion is free-form and keeps the
        # full version so a build is still identifiable.
        $shortVersion = ($Version -split '-')[0]

        # Written beside the publish folder and never inside it.
        #
        # vpk copies everything in --packDir into the .app, so a generated
        # Info.plist left there ships as an application file as well as being the
        # bundle's manifest - and pkgbuild then cannot find exactly one component
        # to take the package identifier from:
        #
        #   pkgbuild: error: No package identifier specified and not exactly one
        #   component to derive it from.
        #
        # It fails at the very last step, after the .app and the portable zip have
        # both been built, which makes it look like an installer problem rather
        # than a stray file. --exclude does not save it.
        $plistDir = Join-Path $repoRoot 'artifacts/macos'
        New-Item -ItemType Directory -Force -Path $plistDir | Out-Null
        $plistPath = Join-Path $plistDir 'Info.plist'

        (Get-Content -Raw 'build/macos/Info.plist.template').
            Replace('__VERSION__', $Version).
            Replace('__SHORT_VERSION__', $shortVersion) |
            Set-Content -NoNewline -Path $plistPath

        # --plist and --bundleId are mutually exclusive in vpk; the identifier is
        # declared in the template instead, and pkgbuild derives the package
        # identifier from it.
        $extraArgs += '--plist', $plistPath

        if ($signPackage) {
            Write-Host "==> Signing as $MacSignAppIdentity and notarising via $MacNotaryProfile" -ForegroundColor Cyan
            $extraArgs += '--signAppIdentity', $MacSignAppIdentity
            $extraArgs += '--signInstallIdentity', $MacSignInstallIdentity
            $extraArgs += '--signEntitlements', $entitlements
            $extraArgs += '--notaryProfile', $MacNotaryProfile
        }
    }

    # vpk refuses to package a build whose Main does not call VelopackApp.Run(),
    # so this also proves the installer hooks are wired before anything ships.
    Write-Host "==> Packaging" -ForegroundColor Cyan
    dotnet vpk pack `
        --packId ClaudeStatus `
        --packVersion $Version `
        --packDir $publishDir `
        --packTitle 'ClaudeStatus' `
        --packAuthors 'ZeroWorks' `
        --mainExe $mainExe `
        --runtime $Runtime `
        --icon $icon `
        --outputDir $releaseDir `
        @extraArgs
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed with exit code $LASTEXITCODE." }

    Write-Host "==> Artifacts in $releaseDir" -ForegroundColor Green
    Get-ChildItem $releaseDir | ForEach-Object {
        '{0,10:N1} MB  {1}' -f ($_.Length / 1MB), $_.Name | Write-Host
    }
}
finally {
    Pop-Location
}
