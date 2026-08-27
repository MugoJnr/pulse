#Requires -Version 5.1
param(
    [string]$ExePath = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $candidates = @(
        Join-Path $env:LOCALAPPDATA "MugoByte\Pulse\Pulse.exe"
        Join-Path $PSScriptRoot "setup\Pulse-Setup.exe"
        Join-Path $PSScriptRoot "publish\Pulse.exe"
    )
    $ExePath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not (Test-Path $ExePath)) {
    throw "Executable not found. Build first: .\build-setup.ps1"
}

$ExePath = (Resolve-Path $ExePath).Path

# Prefer the installed AppData copy when registering startup.
$installed = Join-Path $env:LOCALAPPDATA "MugoByte\Pulse\Pulse.exe"
if ((Test-Path $installed) -and ($ExePath -match 'Pulse-Setup\.exe$')) {
    $ExePath = (Resolve-Path $installed).Path
}

New-ItemProperty `
    -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" `
    -Name "Pulse" `
    -Value "`"$ExePath`"" `
    -PropertyType String `
    -Force | Out-Null

# Clean legacy Run values / tasks from pre-Pulse builds.
foreach ($legacy in @("CpuTempWidget", "MugoByteSystemMonitor")) {
    try {
        Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name $legacy -ErrorAction SilentlyContinue
    } catch {}
}
try {
    Disable-ScheduledTask -TaskName "CpuTempWidget" -ErrorAction SilentlyContinue | Out-Null
} catch {}

Get-Process Pulse, CpuTempWidget -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1
Start-Process $ExePath

Write-Host "Pulse registered for always-run."
Write-Host "Exe: $ExePath"
