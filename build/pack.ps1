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

.PARAMETER WinSignEndpoint
    The Azure Artifact Signing - formerly Trusted Signing - account endpoint, such
    as https://weu.codesigning.azure.net.

    Regional, and it must match the region the account was created in. An endpoint
    for the wrong region is a perfectly valid one: it authenticates, and then reports
    that the account does not exist.

.PARAMETER WinSignAccount
    The name of the Artifact Signing account.

.PARAMETER WinSignProfile
    The name of the certificate profile within that account.

    None of these three is a secret. All three are printed by `signtool verify /pa`
    on any signed download, and none of them authenticates anything on its own: the
    credential is found separately by DefaultAzureCredential - an `az login` on a
    laptop, a federated OIDC token in CI - so no secret is passed to this script or
    written to a file it creates.

.EXAMPLE
    ./build/pack.ps1 -Version 0.1.0

.EXAMPLE
    ./build/pack.ps1 -Version 0.1.0 -Runtime osx-arm64 `
        -MacSignAppIdentity 'Developer ID Application: Example (TEAMID1234)' `
        -MacSignInstallIdentity 'Developer ID Installer: Example (TEAMID1234)' `
        -MacNotaryProfile 'claude-status-notary'

.EXAMPLE
    ./build/pack.ps1 -Version 0.1.0 -Runtime win-x64 `
        -WinSignEndpoint 'https://weu.codesigning.azure.net' `
        -WinSignAccount 'example' `
        -WinSignProfile 'example-public-trust'
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

    [string] $MacNotaryProfile = $env:MAC_NOTARY_PROFILE,

    [string] $WinSignEndpoint = $env:WIN_SIGN_ENDPOINT,

    [string] $WinSignAccount = $env:WIN_SIGN_ACCOUNT,

    [string] $WinSignProfile = $env:WIN_SIGN_PROFILE
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

<#
.SYNOPSIS
    Reports whether a group of signing parameters is complete, and rejects a partial one.

.DESCRIPTION
    Signing is optional; half of it is not. Returns $true for a group with every
    parameter set and $false for an empty one, and throws for anything in between,
    naming what is missing.
#>
function Test-SigningGroup {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Platform,

        [Parameter(Mandatory = $true)]
        [System.Collections.Specialized.OrderedDictionary] $Parameters
    )

    $missing = @($Parameters.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object { $_.Key })

    if ($missing.Count -gt 0 -and $missing.Count -lt $Parameters.Count) {
        throw "$Platform signing needs all of $($Parameters.Keys -join ', '), or none of them. Missing: $($missing -join ', ')."
    }

    return $missing.Count -eq 0
}

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

    # Each platform's parameters are all of them or none of them, decided before
    # anything is built.
    #
    # Every partial combination produces something that looks like it worked and is
    # not shippable: on macOS the app signed but the .pkg not, so the installer is
    # still refused, or both signed but nothing notarised, which Gatekeeper treats as
    # unsigned. Nothing is required, though - an unsigned build is the normal way to
    # test packaging, and a contributor with neither a paid Apple account nor an
    # Azure subscription has to be able to run this script.
    #
    # Checked here rather than beside the vpk arguments they feed, because the publish
    # step sits between the two and takes minutes. A mistyped parameter should cost a
    # second, not a full build that fails at the very end.
    $signMac = Test-SigningGroup -Platform 'macOS' -Parameters ([ordered] @{
        MacSignAppIdentity     = $MacSignAppIdentity
        MacSignInstallIdentity = $MacSignInstallIdentity
        MacNotaryProfile       = $MacNotaryProfile
    })

    $signWin = Test-SigningGroup -Platform 'Windows' -Parameters ([ordered] @{
        WinSignEndpoint = $WinSignEndpoint
        WinSignAccount  = $WinSignAccount
        WinSignProfile  = $WinSignProfile
    })

    # Signing arguments belong to one platform each; vpk rejects the other platform's
    # rather than ignoring them. Someone who releases both ends up with both sets
    # exported in one shell, and without these two checks that shell would break
    # whichever build it was not for, from inside vpk, in a message about arguments
    # rather than about platforms.
    if ($signMac -and -not $packForMac) {
        throw "macOS signing parameters were supplied for runtime '$Runtime'. They apply only to an osx- runtime."
    }

    if ($signWin -and $packForMac) {
        throw "Windows signing parameters were supplied for runtime '$Runtime'. They apply only to a win- runtime."
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
    if ($signMac -and -not (Test-Path $entitlements)) {
        throw "Entitlements file not found at $entitlements."
    }

    if ($packForMac -and -not $signMac) {
        Write-Host "==> Unsigned macOS build; Gatekeeper will refuse it on another machine." -ForegroundColor Yellow
    }

    if (-not $packForMac -and -not $signWin) {
        Write-Host "==> Unsigned Windows build; SmartScreen will warn before it runs." -ForegroundColor Yellow
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
        'src/ClaudeStatus.App/Assets/app-mark.icns'
    } else {
        'src/ClaudeStatus.App/Assets/app-mark.ico'
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

        if ($signMac) {
            Write-Host "==> Signing as $MacSignAppIdentity and notarising via $MacNotaryProfile" -ForegroundColor Cyan
            $extraArgs += '--signAppIdentity', $MacSignAppIdentity
            $extraArgs += '--signInstallIdentity', $MacSignInstallIdentity
            $extraArgs += '--signEntitlements', $entitlements
            $extraArgs += '--notaryProfile', $MacNotaryProfile
        }
    }
    elseif ($signWin) {
        # Azure Artifact Signing, until recently called Trusted Signing. The service
        # issues a fresh certificate per request and each one lives 72 hours, so
        # nothing durable is stored here: there is no .pfx on the machine, no password
        # for one, and no expiry date to diarise. vpk hands this file to the
        # signtool.exe and dlib it bundles.
        #
        # That dlib needs the .NET 8 *runtime* present, which is a separate thing from
        # the SDK in global.json and is not implied by it. Without it signing fails
        # inside signtool with a missing-framework error that names neither Azure nor
        # Velopack.
        #
        # Written beside the publish folder and never inside it, for the same reason
        # as the Info.plist above: vpk copies everything in --packDir into the package,
        # so a metadata file left there would ship to every user. It is not a secret -
        # signtool prints all three values from any signed download - but it is not
        # something to distribute either.
        #
        # utf8NoBOM is explicit because the parser on the other side rejects a BOM,
        # and it is exactly the kind of default that differs between hosts.
        $metadataDir = Join-Path $repoRoot 'artifacts/windows'
        New-Item -ItemType Directory -Force -Path $metadataDir | Out-Null
        $metadataPath = Join-Path $metadataDir 'metadata.json'

        [ordered] @{
            Endpoint               = $WinSignEndpoint
            CodeSigningAccountName = $WinSignAccount
            CertificateProfileName = $WinSignProfile
        } | ConvertTo-Json | Set-Content -Path $metadataPath -Encoding utf8NoBOM

        Write-Host "==> Signing as $WinSignAccount/$WinSignProfile via $WinSignEndpoint" -ForegroundColor Cyan
        $extraArgs += '--azureTrustedSignFile', $metadataPath
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
