<#
.SYNOPSIS
    Removes Dropzone.

.DESCRIPTION
    Stops the app, removes the install folder, the Start Menu shortcut and the autostart
    entry. Your transferred files and settings are kept unless you pass -RemoveData.

.EXAMPLE
    .\uninstall.ps1
    .\uninstall.ps1 -InstallDir 'B:\Dropzone' -RemoveData
#>
[CmdletBinding()]
param(
    [string]$InstallDir = 'B:\Dropzone',
    [switch]$RemoveData
)

$ErrorActionPreference = 'Stop'

function Say($text) { Write-Host "  $text" }

Write-Host "Dropzone uninstaller" -ForegroundColor Cyan

# --- stop -------------------------------------------------------------------
$running = Get-Process -Name 'Dropzone.App' -ErrorAction SilentlyContinue
if ($running) {
    $running | Stop-Process -Force
    Start-Sleep -Seconds 2
    Say "stopped running instance"
}

# --- autostart --------------------------------------------------------------
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Get-ItemProperty -Path $runKey -Name 'Dropzone' -ErrorAction SilentlyContinue) {
    Remove-ItemProperty -Path $runKey -Name 'Dropzone'
    Say "autostart entry removed"
}

# --- shortcut ---------------------------------------------------------------
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Dropzone.lnk'
if (Test-Path $shortcut) {
    Remove-Item $shortcut -Force
    Say "Start Menu shortcut removed"
}

# --- program files ----------------------------------------------------------
if (Test-Path $InstallDir) {
    Remove-Item $InstallDir -Recurse -Force
    Say "removed $InstallDir"
} else {
    Say "nothing installed at $InstallDir"
}

# --- settings and data ------------------------------------------------------
$settings = Join-Path $env:APPDATA 'Dropzone'
$data = Join-Path $env:USERPROFILE 'Dropzone'

if ($RemoveData) {
    foreach ($path in @($settings, $data)) {
        if (Test-Path $path) {
            Remove-Item $path -Recurse -Force
            Say "removed $path"
        }
    }
} else {
    Write-Host "Kept your files and settings:" -ForegroundColor Yellow
    if (Test-Path $settings) { Say $settings }
    if (Test-Path $data) { Say $data }
    Write-Host "  (pass -RemoveData to delete them too)"
}

Write-Host "Done." -ForegroundColor Green
