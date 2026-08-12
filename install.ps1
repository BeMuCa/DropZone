<#
.SYNOPSIS
    Installs DropZone.

.DESCRIPTION
    Copies the published build to the install folder, adds a Start Menu shortcut and
    optionally starts DropZone with Windows. No administrator rights are needed as long
    as the install folder is writable.

.EXAMPLE
    .\install.ps1
    .\install.ps1 -InstallDir 'B:\DropZone' -Autostart
    .\install.ps1 -Source .\artifact
#>
[CmdletBinding()]
param(
    [string]$InstallDir = 'B:\DropZone',
    [string]$Source,
    [switch]$Autostart
)

$ErrorActionPreference = 'Stop'

function Say($text) { Write-Host "  $text" }

Write-Host "DropZone installer" -ForegroundColor Cyan

# --- locate the build -------------------------------------------------------
if (-not $Source) {
    $candidates = @(
        (Join-Path $PSScriptRoot 'artifact'),
        (Join-Path $PSScriptRoot 'src\DropZone.App\bin\Release\net10.0-windows\win-x64\publish'),
        (Join-Path $PSScriptRoot 'publish')
    )
    $Source = $candidates | Where-Object { Test-Path (Join-Path $_ 'DropZone.App.exe') } | Select-Object -First 1
}

if (-not $Source -or -not (Test-Path (Join-Path $Source 'DropZone.App.exe'))) {
    throw "Could not find DropZone.App.exe. Pass -Source <folder> pointing at the published build."
}

Say "source: $Source"

# --- prerequisite -----------------------------------------------------------
# Missing dotnet is the normal case for someone who only downloaded the release, so this
# check must never be the thing that stops the install.
$runtimeFound = $false
if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    try {
        $runtimeFound = [bool](& dotnet --list-runtimes 2>$null |
            Select-String 'Microsoft\.WindowsDesktop\.App 10\.')
    } catch {
        $runtimeFound = $false
    }
}

if (-not $runtimeFound) {
    Write-Warning "The .NET 10 Desktop Runtime was not detected. DropZone needs it to run."
    Write-Warning "Install it with:  winget install Microsoft.DotNet.DesktopRuntime.10"
}

# --- stop a running copy ----------------------------------------------------
$running = Get-Process -Name 'DropZone.App' -ErrorAction SilentlyContinue
if ($running) {
    Say "stopping running instance"
    $running | Stop-Process -Force
    Start-Sleep -Seconds 2
}

# --- ask where to put it ----------------------------------------------------
# Only when the caller did not say; passing -InstallDir keeps this fully scriptable.
if (-not $PSBoundParameters.ContainsKey('InstallDir') -and [Environment]::UserInteractive) {
    $answer = Read-Host "Install location [$InstallDir]"
    if ($answer) { $InstallDir = $answer }
}

# --- check the target drive exists -----------------------------------------
$root = [System.IO.Path]::GetPathRoot($InstallDir)
if (-not (Test-Path $root)) {
    throw "Drive $root is not available. Pass -InstallDir with a path that exists."
}

# --- copy -------------------------------------------------------------------
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Copy-Item -Path (Join-Path $Source '*') -Destination $InstallDir -Recurse -Force
Say "installed to $InstallDir"

# The uninstaller belongs next to the program, not wherever the zip was unpacked —
# that folder is usually long gone by the time someone wants to remove this.
$uninstaller = Join-Path $Source 'uninstall.ps1'
if (-not (Test-Path $uninstaller)) { $uninstaller = Join-Path $PSScriptRoot 'uninstall.ps1' }
if (Test-Path $uninstaller) {
    Copy-Item $uninstaller -Destination $InstallDir -Force
    Say "uninstaller placed in $InstallDir"
} else {
    Write-Warning "uninstall.ps1 not found — you will have to delete $InstallDir by hand."
}

$exe = Join-Path $InstallDir 'DropZone.App.exe'

# --- Start Menu shortcut ----------------------------------------------------
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$shortcut = Join-Path $startMenu 'DropZone.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $InstallDir
$link.Description = 'DropZone — phone and PC file transfer'
$link.Save()
Say "Start Menu shortcut created"

# --- autostart --------------------------------------------------------------
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ($Autostart) {
    Set-ItemProperty -Path $runKey -Name 'DropZone' -Value "`"$exe`""
    Say "will start with Windows"
} else {
    if (Get-ItemProperty -Path $runKey -Name 'DropZone' -ErrorAction SilentlyContinue) {
        Remove-ItemProperty -Path $runKey -Name 'DropZone'
    }
    Say "autostart not enabled (pass -Autostart to enable)"
}

Write-Host "Done. Launch it from the Start Menu, or run:" -ForegroundColor Green
Write-Host "  $exe"
