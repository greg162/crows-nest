<#
.SYNOPSIS
    Builds a Crowsnest release and creates it on GitHub as drafts.

.DESCRIPTION
    Builds on this PC, not on GitHub: the host needs the MSFS 2024 SDK (MSFS2024_SDK) for the
    SimConnect DLLs, which the MSI ships but the repo never holds.

    1. Checks the tree is clean and pushed, and the firmware build is the version in
       firmware/crowsnest-display/CMakeLists.txt (run idf.py build first after a bump).
    2. Runs the host tests.
    3. Builds the MSI (host/src/Crowsnest.Installer), which carries the firmware image too.
    4. Zips the flasher with that image, for anyone who only wants firmware.
    5. Creates two draft releases with the GitHub CLI: app-v<app version> with the MSI, and
       firmware-v<firmware version> with the zip (skipped when that firmware is already
       released). Nothing is public until a draft is published on GitHub.

    The files land in artifacts/release/<app version>/ either way.

.PARAMETER BuildOnly
    Build the files but don't create releases.

.PARAMETER AllowDirty
    Build from a tree with uncommitted changes. Only with -BuildOnly: a release must be
    of a commit that is on GitHub.

.EXAMPLE
    .\tools\Build-Release.ps1

.EXAMPLE
    .\tools\Build-Release.ps1 -BuildOnly -AllowDirty
#>
[CmdletBinding()]
param(
    [switch]$BuildOnly,
    [switch]$AllowDirty
)

Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'

$Repo = Split-Path $PSScriptRoot -Parent
$Installer = Join-Path $Repo 'host\src\Crowsnest.Installer'
$Flasher = Join-Path $Repo 'flasher'

function Write-Step([string]$text) {
    Write-Host ''
    Write-Host $text -ForegroundColor Cyan
}

function Stop-WithMessage([string]$text) {
    Write-Host ''
    Write-Host $text -ForegroundColor Red
    exit 1
}

# Whether a native command succeeds, its output dropped. Windows PowerShell turns a native
# command's redirected stderr into an error, which -ErrorAction Stop would end the script on.
function Test-Command([scriptblock]$command) {
    $ErrorActionPreference = 'Continue'
    & $command 2>&1 | Out-Null
    return $LASTEXITCODE -eq 0
}

# UTF-8 without the byte order mark Windows PowerShell's Set-Content adds, which GitHub would show.
function Write-Utf8([string]$path, [Parameter(ValueFromPipeline)][string]$text) {
    [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding $false))
}

function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { Stop-WithMessage "$what failed." }
}

if ($AllowDirty -and -not $BuildOnly) { Stop-WithMessage '-AllowDirty only goes with -BuildOnly.' }

# --- Versions ------------------------------------------------------------------------

$appVersion = [regex]::Match((Get-Content -Raw (Join-Path $Repo 'host\Directory.Build.props')), '<Version>([^<]+)</Version>').Groups[1].Value
$firmwareVersion = [regex]::Match((Get-Content -Raw (Join-Path $Repo 'firmware\crowsnest-display\CMakeLists.txt')), 'set\(PROJECT_VER "([^"]+)"\)').Groups[1].Value
if (-not $appVersion -or -not $firmwareVersion) { Stop-WithMessage 'Could not read the app or firmware version.' }
$appTag = "app-v$appVersion"
$firmwareTag = "firmware-v$firmwareVersion"
$out = Join-Path $Repo "artifacts\release\$appVersion"

Write-Host ''
Write-Host "Crowsnest release: app $appVersion, firmware $firmwareVersion" -ForegroundColor White

# --- Checks --------------------------------------------------------------------------

Write-Step 'Checking...'
if (-not $env:MSFS2024_SDK) { Stop-WithMessage 'Set MSFS2024_SDK to the MSFS 2024 SDK folder (with a trailing backslash).' }

$gh = $null
if (-not $BuildOnly) {
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if (-not $gh) { Stop-WithMessage 'The GitHub CLI is not installed: winget install GitHub.cli, then gh auth login. Or use -BuildOnly.' }
    if (-not (Test-Command { gh auth status })) { Stop-WithMessage 'The GitHub CLI is not logged in: run gh auth login.' }
}

if (-not $AllowDirty) {
    if (git -C $Repo status --porcelain) { Stop-WithMessage 'The tree has uncommitted changes. Commit them first.' }
    git -C $Repo fetch --quiet origin
    $head = git -C $Repo rev-parse HEAD
    if (-not (Test-Command { git -C $Repo merge-base --is-ancestor $head origin/main })) { Stop-WithMessage 'This commit is not on GitHub yet. Push it first.' }
}

if ($gh) {
    if (Test-Command { gh release view $appTag }) { Stop-WithMessage "$appTag already exists on GitHub. Bump the version in host\Directory.Build.props." }
}

# --- Build ---------------------------------------------------------------------------

Write-Step 'Running the host tests...'
Invoke-Checked 'The tests' { dotnet test (Join-Path $Repo 'host\Crowsnest.slnx') -c Release -nologo -v q }

Write-Step 'Building the MSI (it also checks the firmware build is the right version)...'
Invoke-Checked 'The MSI build' { dotnet build $Installer -c Release -nologo -v q }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

$msi = Join-Path $Installer "bin\Release\Crowsnest-$appVersion.msi"
Copy-Item $msi $out

# The image the MSI build exported, so both downloads carry the same firmware.
$image = @(Get-ChildItem (Join-Path $Installer 'obj\firmware\Release') -Filter 'crowsnest-display-*.bin')
if ($image.Count -ne 1 -or $image[0].Name -ne "crowsnest-display-$firmwareVersion.bin") {
    Stop-WithMessage "Expected crowsnest-display-$firmwareVersion.bin from the MSI build, found: $($image.Name -join ', ')"
}

Write-Step 'Zipping the flasher...'
$zip = Join-Path $out "Crowsnest-Flasher-$firmwareVersion.zip"
# Entries added one by one, named with forward slashes: Compress-Archive and ZipFile in
# Windows PowerShell both write backslashes, which unzip tools elsewhere mishandle.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in (Join-Path $Flasher 'Flash-Crowsnest.cmd'), (Join-Path $Flasher 'Flash-Crowsnest.ps1'), (Join-Path $Flasher 'README.md'), $image[0].FullName) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file, "Crowsnest-Flasher/$(Split-Path $file -Leaf)", [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally {
    $archive.Dispose()
}

$sums = Get-ChildItem $out -File | ForEach-Object {
    $sha = [Security.Cryptography.SHA256]::Create()
    $file = [IO.File]::OpenRead($_.FullName)
    try { $hash = -join ($sha.ComputeHash($file) | ForEach-Object { $_.ToString('x2') }) } finally { $file.Dispose(); $sha.Dispose() }
    "$hash  $($_.Name)"
}
$sums | Set-Content (Join-Path $out 'SHA256SUMS.txt') -Encoding ascii

# --- Release notes -------------------------------------------------------------------

$appNotes = Join-Path $out 'notes-app.md'
@"
Crowsnest $appVersion for Microsoft Flight Simulator 2024. Comes with firmware $firmwareVersion for the boards.

## Install

Download ``Crowsnest-$appVersion.msi`` and run it. It installs for you only, so it doesn't ask for administrator rights, and it replaces an older version.

The installer isn't code-signed, so Windows may say "Windows protected your PC". Click **More info**, then **Run anyway**.

## Firmware

The tray menu's **Update firmware...** puts firmware $firmwareVersion on your boards, and Crowsnest tells you when a board is behind. ``Crowsnest-Flasher-$firmwareVersion.zip`` in the $firmwareTag release is the same flasher, on its own.

## What's new

- (edit before publishing)
"@ | Write-Utf8 $appNotes

$firmwareNotes = Join-Path $out 'notes-firmware.md'
@"
Firmware $firmwareVersion for the CrowPanel boards.

Crowsnest $appVersion and later already include it: use **Update firmware...** in the tray menu. This zip is the flasher on its own: unzip it, plug the boards in, and double-click ``Flash-Crowsnest.cmd``.

## What's new

- (edit before publishing)
"@ | Write-Utf8 $firmwareNotes

if ($BuildOnly) {
    Write-Step "Built in $out"
    Get-ChildItem $out -File | ForEach-Object { Write-Host "  $($_.Name)" }
    exit 0
}

# --- GitHub --------------------------------------------------------------------------

$target = git -C $Repo rev-parse HEAD

Write-Step "Creating the draft release $appTag..."
Invoke-Checked "Creating $appTag" {
    gh release create $appTag $msi (Join-Path $out 'SHA256SUMS.txt') --draft --latest --target $target --title "Crowsnest $appVersion" --notes-file $appNotes
}

if (Test-Command { gh release view $firmwareTag }) {
    Write-Step "$firmwareTag is already on GitHub, so no firmware release this time."
} else {
    Write-Step "Creating the draft release $firmwareTag..."
    Invoke-Checked "Creating $firmwareTag" {
        gh release create $firmwareTag $zip --draft --latest=false --target $target --title "Firmware $firmwareVersion" --notes-file $firmwareNotes
    }
}

Write-Step 'Done. The drafts are at:'
Write-Host "  $(gh release view $appTag --json url --jq .url)"
Write-Host "  $(gh release view $firmwareTag --json url --jq .url)"
Write-Host ''
Write-Host 'Fill in "What''s new" on each, then publish: the tags are created then.'
