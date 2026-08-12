<#
.SYNOPSIS
    Installs Dropzone.

.DESCRIPTION
    Copies the published build to the install folder, adds a Start Menu shortcut and
    optionally starts Dropzone with Windows. No administrator rights are needed as long
    as the install folder is writable.

.EXAMPLE
    .\install.ps1
    .\install.ps1 -InstallDir 'B:\Dropzone' -Autostart
    .\install.ps1 -Source .\artifact
#>
[CmdletBinding()]
param(
    [string]$InstallDir = 'B:\Dropzone',
    [string]$Source,
    [switch]$Autostart
)

$ErrorActionPreference = 'Stop'

function Say($text) { Write-Host "  $text" }

Write-Host "Dropzone installer" -ForegroundColor Cyan

# --- locate the build -------------------------------------------------------
if (-not $Source) {
    $candidates = @(
        (Join-Path $PSScriptRoot 'artifact'),
        (Join-Path $PSScriptRoot 'src\Dropzone.App\bin\Release\net10.0-windows\win-x64\publish'),
        (Join-Path $PSScriptRoot 'publish')
    )
    $Source = $candidates | Where-Object { Test-Path (Join-Path $_ 'Dropzone.App.exe') } | Select-Object -First 1
}

if (-not $Source -or -not (Test-Path (Join-Path $Source 'Dropzone.App.exe'))) {
    throw "Could not find Dropzone.App.exe. Pass -Source <folder> pointing at the published build."
}

Say "source: $Source"

# --- prerequisite -----------------------------------------------------------
$runtime = & dotnet --list-runtimes 2>$null | Select-String 'Microsoft.WindowsDesktop.App 10\.'
if (-not $runtime) {
    Write-Warning "The .NET 10 Desktop Runtime was not detected. Dropzone needs it to run."
    Write-Warning "Install it with:  winget install Microsoft.DotNet.DesktopRuntime.10"
}

# --- stop a running copy ----------------------------------------------------
$running = Get-Process -Name 'Dropzone.App' -ErrorAction SilentlyContinue
if ($running) {
    Say "stopping running instance"
    $running | Stop-Process -Force
    Start-Sleep -Seconds 2
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

$exe = Join-Path $InstallDir 'Dropzone.App.exe'

# --- Start Menu shortcut ----------------------------------------------------
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$shortcut = Join-Path $startMenu 'Dropzone.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $InstallDir
$link.Description = 'Dropzone — phone and PC file transfer'
$link.Save()
Say "Start Menu shortcut created"

# --- autostart --------------------------------------------------------------
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ($Autostart) {
    Set-ItemProperty -Path $runKey -Name 'Dropzone' -Value "`"$exe`""
    Say "will start with Windows"
} else {
    if (Get-ItemProperty -Path $runKey -Name 'Dropzone' -ErrorAction SilentlyContinue) {
        Remove-ItemProperty -Path $runKey -Name 'Dropzone'
    }
    Say "autostart not enabled (pass -Autostart to enable)"
}

Write-Host "Done. Launch it from the Start Menu, or run:" -ForegroundColor Green
Write-Host "  $exe"
