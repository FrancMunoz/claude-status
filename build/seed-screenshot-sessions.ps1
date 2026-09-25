#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Gives a running ClaudeStatus the scripted session set the screenshots pose for.

.DESCRIPTION
    Writes spool files - the same drop box Claude Code's hooks write to - so the
    app sees three sessions open and two of them working, with the same folder
    names on every platform. That is what makes the macOS menu bar and the Windows
    taskbar widget both read 2/3 and list the same three projects, so their
    screenshots can be put side by side.

    The app must already be running with the session watch on. Nothing here talks
    to it directly: the spool is a directory, and the app drains it within twenty
    seconds.

    Only the last path segment is ever shown, so the roots differ per platform
    and the names do not. See docs/screenshots.md for the whole procedure.

.PARAMETER Clear
    Close the scripted sessions again instead of opening them, and empty the
    spool. Run this when the shoot is over.

.PARAMETER ConfigDirectory
    Override where the spool lives. Only needed if CLAUDE_STATUS_CONFIG_DIR-style
    redirection is in play; by default this is worked out per platform exactly as
    the app works it out.

.EXAMPLE
    ./build/seed-screenshot-sessions.ps1
    Three sessions open, two working. Wait ~20 s, then take the screenshot.

.EXAMPLE
    ./build/seed-screenshot-sessions.ps1 -Clear
    Put it back.
#>
[CmdletBinding()]
param(
    [switch] $Clear,
    [string] $ConfigDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Where the app keeps its own files, per platform. Mirrors the ConfigDirectory
# properties on WindowsPlatformInfo / MacOsPlatformInfo / LinuxPlatformInfo -
# including the lower-case folder name Linux uses by convention.
function Get-ConfigDirectory {
    if ($ConfigDirectory) { return $ConfigDirectory }

    if ($IsWindows) {
        return Join-Path $env:APPDATA 'ClaudeStatus'
    }

    if ($IsMacOS) {
        return Join-Path $HOME 'Library/Application Support/ClaudeStatus'
    }

    $xdg = $env:XDG_CONFIG_HOME
    if (-not $xdg) { $xdg = Join-Path $HOME '.config' }
    return Join-Path $xdg 'claudestatus'
}

# The root differs per platform and is never shown; the leaf is the session's
# name in every list, so all three are identical wherever this runs.
function Get-ProjectRoot {
    if ($IsWindows) { return 'C:\Proyectos' }
    return (Join-Path $HOME 'Documents/GitHub')
}

# Three sessions, two of them mid-turn. Two working over three open is the
# reading worth photographing: it shows the badge counting both, which "1/1"
# cannot, and it leaves one session visibly waiting.
#
# OpenedMinutesAgo only sets how long each has been up. Distinct values, so the
# list does not read as three copies of one row; the durations go on ticking
# after this runs, which is why docs/screenshots.md says to shoot promptly.
$sessions = @(
    [pscustomobject]@{ Id = 'screenshot-1'; Folder = 'claude-status'; OpenedMinutesAgo = 41; Working = $true }
    [pscustomobject]@{ Id = 'screenshot-2'; Folder = 'notes';         OpenedMinutesAgo = 12; Working = $true }
    [pscustomobject]@{ Id = 'screenshot-3'; Folder = 'site';          OpenedMinutesAgo = 5;  Working = $false }
)

$configDirectory = Get-ConfigDirectory
$spool = Join-Path $configDirectory 'session-events'
$root = Get-ProjectRoot

if (-not (Test-Path $configDirectory)) {
    throw "No ClaudeStatus config directory at '$configDirectory'. Run the app once first."
}

New-Item -ItemType Directory -Path $spool -Force | Out-Null

# The app reads the spool in ordinal filename order, so the sequence number is
# not decoration: a Started that lands after its own Submitted would put the
# session back to merely open, and the badge would read one short. Real hooks
# order themselves by the timestamp they lead with; these lead with a counter
# for the same reason, and are written to a dot-prefixed name first so the app's
# watcher cannot read one while it is still being written.
$sequence = 0
function Write-Event {
    param(
        [Parameter(Mandatory)] [string] $Kind,
        [Parameter(Mandatory)] [string] $Id,
        [Parameter(Mandatory)] [string] $Folder,
        [Parameter(Mandatory)] [datetime] $At
    )

    $script:sequence++
    $name = ('screenshot-{0:d3}-{1}.json' -f $script:sequence, [guid]::NewGuid().ToString('n'))
    $payload = [ordered]@{
        Kind   = $Kind
        Id     = $Id
        Folder = $Folder
        At     = $At.ToUniversalTime().ToString('o')
        Origin = $null
    } | ConvertTo-Json -Compress

    $final = Join-Path $spool $name
    $temporary = Join-Path $spool ".$name"
    Set-Content -LiteralPath $temporary -Value $payload -Encoding utf8 -NoNewline
    Move-Item -LiteralPath $temporary -Destination $final -Force
}

$now = [datetime]::UtcNow

if ($Clear) {
    Get-ChildItem -LiteralPath $spool -Filter 'screenshot-*.json' -ErrorAction SilentlyContinue |
        Remove-Item -Force

    foreach ($session in $sessions) {
        Write-Event -Kind 'Ended' -Id $session.Id -Folder (Join-Path $root $session.Folder) -At $now
    }

    Write-Host "Closed $($sessions.Count) scripted sessions. The app drops them within 20 s." -ForegroundColor Cyan
    return
}

foreach ($session in $sessions) {
    Write-Event -Kind 'Started' -Id $session.Id `
        -Folder (Join-Path $root $session.Folder) `
        -At $now.AddMinutes(-$session.OpenedMinutesAgo)
}

foreach ($session in $sessions | Where-Object Working) {
    Write-Event -Kind 'Submitted' -Id $session.Id `
        -Folder (Join-Path $root $session.Folder) `
        -At $now
}

$working = ($sessions | Where-Object Working).Count
Write-Host "Seeded $working/$($sessions.Count) into $spool." -ForegroundColor Cyan
Write-Host 'Wait about 20 s for the app to drain the spool, then take the screenshot.'
