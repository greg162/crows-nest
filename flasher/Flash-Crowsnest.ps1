<#
.SYNOPSIS
    Puts Crowsnest firmware on CrowPanel boards over USB.

.DESCRIPTION
    Lists the CrowPanels plugged into this PC, asks which to flash, and flashes them one at a
    time with Espressif's esptool, which it downloads the first time (no ESP-IDF or Python
    needed). Crowsnest is closed while it works, because it holds the boards' ports, and
    started again afterwards.

    The firmware is a crowsnest-display-<version>.bin next to this script. Run from the
    repo with none there, it uses the last firmware build instead.

    Easiest is to double-click Flash-Crowsnest.cmd, which runs this script.

.PARAMETER Image
    The firmware file to flash, instead of looking for one.

.PARAMETER Port
    Flash these ports (COM4, COM5) or "all" without asking.

.PARAMETER ExportImage
    Flash nothing: copy the firmware image it would use into this folder. The installer's
    build uses it to ship the image beside the flasher. From the repo, the firmware build has
    to be of the version in CMakeLists.txt.

.EXAMPLE
    .\Flash-Crowsnest.ps1

.EXAMPLE
    .\Flash-Crowsnest.ps1 -Port COM4
#>
[CmdletBinding()]
param(
    [string]$Image,
    [string[]]$Port,
    [string]$ExportImage
)

# Runs in Windows PowerShell 5.1 as well as PowerShell 7: most PCs only have 5.1.
Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'

# esptool 5.3.1 is the version ESP-IDF 6.1 flashes these boards with. The hash is the one
# GitHub publishes for the release asset.
$EsptoolVersion = '5.3.1'
$EsptoolUrl = "https://github.com/espressif/esptool/releases/download/v$EsptoolVersion/esptool-v$EsptoolVersion-windows-amd64.zip"
$EsptoolSha256 = '2b4a73c45db27426685896f64ce3e557f63a64f43cc100cb65c0cc3486af96d3'
$EsptoolDir = Join-Path $env:LOCALAPPDATA "Crowsnest\esptool\$EsptoolVersion"

# The CrowPanel's ESP32-S3 USB port. Windows names the serial interface MI_00; its parent
# device's serial number is the board's MAC, which is also its Crowsnest id.
$BoardUsbId = 'USB\VID_303A&PID_1001'

# The firmware image starts at flash address 0 (bootloader, partition table, app). The
# app is at 0x20000 (partitions.csv), and ESP-IDF's app description sits 0x20 into it.
$AppOffset = 0x20000
$AppDescMagic = 0xABCD5432L   # L: a plain 0xABCD5432 is a negative Int32 in PowerShell
$ProjectName = 'crowsnest-display'

$RepoBuildDir = Join-Path $PSScriptRoot '..\firmware\crowsnest-display\build'
$RepoCMakeLists = Join-Path $PSScriptRoot '..\firmware\crowsnest-display\CMakeLists.txt'

function Write-Heading([string]$text) {
    Write-Host ''
    Write-Host $text -ForegroundColor Cyan
}

function Stop-WithMessage([string]$text) {
    Write-Host ''
    Write-Host $text -ForegroundColor Red
    exit 1
}

# The version and project name from a firmware image's app description, or $null when the
# file is not a Crowsnest image. $appStart is where the app begins in the file: $AppOffset in
# a full image, 0 in the build's app-only crowsnest-display.bin.
function Read-FirmwareInfo([string]$path, [int]$appStart = $AppOffset) {
    $stream = [IO.File]::OpenRead($path)
    try {
        $buffer = New-Object byte[] 0x60
        $stream.Position = $appStart + 0x20
        if ($stream.Read($buffer, 0, $buffer.Length) -ne $buffer.Length) { return $null }
    } finally {
        $stream.Dispose()
    }
    if ([BitConverter]::ToUInt32($buffer, 0) -ne $AppDescMagic) { return $null }
    $text = { param($offset) [Text.Encoding]::ASCII.GetString($buffer, $offset, 32).TrimEnd([char]0) }
    $info = [pscustomobject]@{ Version = (& $text 0x10); Project = (& $text 0x30); Path = $path }
    if ($info.Project -ne $ProjectName) { return $null }
    return $info
}

function Get-Esptool {
    $exe = Join-Path $EsptoolDir 'esptool.exe'
    if (Test-Path $exe) { return $exe }

    Write-Heading "Getting esptool $EsptoolVersion from Espressif (about 65 MB, only this once)..."
    New-Item -ItemType Directory -Force $EsptoolDir | Out-Null
    $zip = Join-Path $EsptoolDir 'esptool.zip'
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $oldProgress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'   # Windows PowerShell's progress bar slows downloads to a crawl
    try {
        Invoke-WebRequest -Uri $EsptoolUrl -OutFile $zip -UseBasicParsing
    } catch {
        Stop-WithMessage "Couldn't download esptool: $($_.Exception.Message)`nCheck the internet connection and try again."
    } finally {
        $ProgressPreference = $oldProgress
    }
    # .NET rather than Get-FileHash, which Windows PowerShell can't load when started from
    # PowerShell 7 (its module path then points at 7's modules).
    $sha = [Security.Cryptography.SHA256]::Create()
    $file = [IO.File]::OpenRead($zip)
    try { $hash = -join ($sha.ComputeHash($file) | ForEach-Object { $_.ToString('x2') }) } finally { $file.Dispose(); $sha.Dispose() }
    if ($hash -ne $EsptoolSha256) {
        Remove-Item $zip
        Stop-WithMessage 'The esptool download did not match its published checksum, so it was deleted. Try again later.'
    }
    # Only esptool itself (and its licence); the zip also has espefuse and espsecure.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($entry in $archive.Entries) {
            if ($entry.Name -in 'esptool.exe', 'LICENSE') {
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $EsptoolDir $entry.Name), $true)
            }
        }
    } finally {
        $archive.Dispose()
    }
    Remove-Item $zip
    Write-Host 'Done.'
    return $exe
}

# Joins the repo's last firmware build into one image, as flash_args lists it.
function New-ImageFromBuild([string]$esptool) {
    $flashArgs = Get-Content (Join-Path $RepoBuildDir 'flash_args')
    $app = Read-FirmwareInfo (Join-Path $RepoBuildDir 'crowsnest-display.bin') 0
    if (-not $app) { Stop-WithMessage "The firmware build in $RepoBuildDir has no Crowsnest app in it." }
    $output = Join-Path $RepoBuildDir "crowsnest-display-$($app.Version).bin"
    $mergeArgs = @('--chip', 'esp32s3', 'merge-bin', '-o', $output) + ($flashArgs[0] -split ' ')
    foreach ($line in $flashArgs | Select-Object -Skip 1) {
        if ($line.Trim()) {
            $offset, $file = $line.Trim() -split ' ', 2
            $mergeArgs += $offset, (Join-Path $RepoBuildDir $file)
        }
    }
    & $esptool @mergeArgs | Out-Null
    if ($LASTEXITCODE -ne 0) { Stop-WithMessage "esptool couldn't join the firmware build into one image." }
    return $output
}

function Find-Image([string]$esptool) {
    if ($Image) {
        $info = Read-FirmwareInfo (Resolve-Path $Image).Path
        if (-not $info) { Stop-WithMessage "$Image isn't a full Crowsnest firmware image (crowsnest-display-<version>.bin)." }
        return $info
    }
    $beside = @(Get-ChildItem $PSScriptRoot -Filter 'crowsnest-display-*.bin' |
        ForEach-Object { Read-FirmwareInfo $_.FullName } | Where-Object { $_ })
    if ($beside.Count -gt 0) {
        return $beside | Sort-Object { [version]($_.Version -replace '[^0-9.].*$', '') } | Select-Object -Last 1
    }
    if (Test-Path (Join-Path $RepoBuildDir 'flash_args')) {
        Write-Host 'No firmware file next to this script, so using the last firmware build.'
        return Read-FirmwareInfo (New-ImageFromBuild $esptool)
    }
    Stop-WithMessage "No firmware found. Put crowsnest-display-<version>.bin in $PSScriptRoot and run this again."
}

# In port order, by number, so COM10 comes after COM9.
function Get-Boards {
    $serialPorts = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
        Where-Object { $_.InstanceId -like "$BoardUsbId&MI_00\*" })
    $boards = foreach ($device in $serialPorts) {
        $portName = (Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Enum\$($device.InstanceId)\Device Parameters" -Name PortName -ErrorAction SilentlyContinue).PortName
        if (-not $portName) { continue }
        $parent = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName DEVPKEY_Device_Parent).Data
        $mac = ($parent -split '\\')[-1] -replace ':', ''
        $shortId = if ($mac -match '^[0-9A-Fa-f]{12}$') { $mac.Substring(6).ToLowerInvariant() } else { '?' }
        [pscustomobject]@{ Port = $portName; Id = $shortId }
    }
    $boards | Sort-Object { [int]($_.Port -replace '\D', '') }
}

function Select-Boards([object[]]$boards) {
    if ($Port) {
        if ($Port -contains 'all') { return $boards }
        $chosen = @($boards | Where-Object { $Port -contains $_.Port })
        $missing = @($Port | Where-Object { $_ -notin $boards.Port })
        if ($missing.Count -gt 0) { Stop-WithMessage "No CrowPanel on $($missing -join ', ')." }
        return $chosen
    }

    Write-Heading 'CrowPanels plugged in:'
    for ($i = 0; $i -lt $boards.Count; $i++) {
        Write-Host ('  {0}  {1,-6} board {2}' -f ($i + 1), $boards[$i].Port, $boards[$i].Id)
    }
    Write-Host '  (The id is on the board''s waiting screen, as "PANEL dccc6c", and in Crowsnest''s settings window.)' -ForegroundColor DarkGray
    Write-Host ''

    if ($boards.Count -eq 1) {
        $answer = Read-Host "Flash board $($boards[0].Id) on $($boards[0].Port)? [Y/n]"
        if ($answer -match '^(|y|yes)$') { return $boards }
        return @()
    }
    while ($true) {
        $answer = (Read-Host "Which board? Enter its number, A for all, or Q to quit").Trim()
        if ($answer -match '^q') { return @() }
        if ($answer -match '^a') { return $boards }
        $number = 0
        if ([int]::TryParse($answer, [ref]$number) -and $number -ge 1 -and $number -le $boards.Count) {
            return @($boards[$number - 1])
        }
        Write-Host "Type a number from 1 to $($boards.Count), A or Q."
    }
}

# --- Start ---------------------------------------------------------------------------

Write-Host ''
Write-Host 'Crowsnest firmware flasher' -ForegroundColor White
Write-Host '--------------------------'

$esptool = Get-Esptool
$firmware = Find-Image $esptool
if ($ExportImage) {
    # A release must not ship an image from before the last version bump.
    if ((Test-Path $RepoBuildDir) -and $firmware.Path.StartsWith((Resolve-Path $RepoBuildDir).Path)) {
        $wanted = [regex]::Match((Get-Content -Raw $RepoCMakeLists), 'set\(PROJECT_VER "([^"]+)"\)').Groups[1].Value
        if ($wanted -and $wanted -ne $firmware.Version) {
            Stop-WithMessage "The firmware build is $($firmware.Version) but CMakeLists.txt says $wanted. Run idf.py build first."
        }
    }
    New-Item -ItemType Directory -Force $ExportImage | Out-Null
    Copy-Item $firmware.Path $ExportImage
    Write-Host "Firmware $($firmware.Version) copied to $ExportImage"
    exit 0
}
Write-Host ''
Write-Host "Firmware $($firmware.Version)  ($(Split-Path $firmware.Path -Leaf))"

$boards = @(Get-Boards)
while ($boards.Count -eq 0) {
    if ($Port) { Stop-WithMessage 'No CrowPanel is plugged in.' }
    Write-Host ''
    Write-Host 'No CrowPanel found. Plug one in with a USB-C data cable (some cables only charge).'
    if ((Read-Host 'Press Enter to look again, or Q to quit') -match '^q') { exit 0 }
    $boards = @(Get-Boards)
}

$chosen = @(Select-Boards $boards)
if ($chosen.Count -eq 0) {
    Write-Host 'Nothing flashed.'
    exit 0
}

# Crowsnest holds every board's port open, so esptool can't reach them while it runs.
$tray = @(Get-Process -Name Crowsnest -ErrorAction SilentlyContinue)
$trayPath = $null
if ($tray.Count -gt 0) {
    $trayPath = $tray[0].Path
    Write-Host ''
    Write-Host 'Closing Crowsnest while the boards are flashed. It will start again afterwards.'
    $tray | Stop-Process -Force
    $tray | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

# One board at a time: several boards rewriting flash at once can draw more than a hub gives.
$results = @()
foreach ($board in $chosen) {
    Write-Heading "Flashing board $($board.Id) on $($board.Port)..."
    & $esptool --chip esp32s3 --port $board.Port write-flash 0x0 $firmware.Path
    $results += [pscustomobject]@{ Board = $board; Ok = ($LASTEXITCODE -eq 0) }
}

if ($trayPath) {
    Start-Process $trayPath
}

Write-Heading 'Done:'
foreach ($result in $results) {
    if ($result.Ok) {
        Write-Host "  board $($result.Board.Id) on $($result.Board.Port): firmware $($firmware.Version)" -ForegroundColor Green
    } else {
        Write-Host "  board $($result.Board.Id) on $($result.Board.Port): FAILED" -ForegroundColor Red
    }
}
if (@($results | Where-Object { -not $_.Ok }).Count -gt 0) {
    Write-Host ''
    Write-Host 'If a board failed:'
    Write-Host '  - "Access is denied" or "could not open port": another program has the port open'
    Write-Host '    (Crowsnest DevConsole, a serial monitor, or RGB lighting software). Close it and run this again.'
    Write-Host '  - Otherwise unplug the board, plug it back in, and run this again. A board that was'
    Write-Host '    interrupted mid-flash is not harmed: flashing it again puts it right.'
    Write-Host '  - Still stuck: hold the BOOT button while plugging the board in, then run this again.'
    exit 1
}
