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

.EXAMPLE
    ./build/pack.ps1 -Version 0.1.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string] $Version,

    [string] $Runtime = 'win-x64',

    [string] $OutputDir = 'artifacts/releases'
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
        'src/ClaudeStatus.App/Assets/avalonia-logo.ico'
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
        # declared in the template instead.
        $extraArgs += '--plist', $plistPath
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
