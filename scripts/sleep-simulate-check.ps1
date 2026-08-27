# Sleep / power simulate check (Pulse 1.12.0 deliverable H).
# a) Battery gate for physical SetSuspendState
# b) power.log presence OR PowerResilienceTests
# c) Marks Simulate PASS when SimulateTransition path is verified via tests
#Requires -Version 5.1
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $root "Pulse.sln"))) {
    $root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}
Set-Location $root

function Get-BatteryPercent {
    try {
        $b = Get-CimInstance -ClassName Win32_Battery -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $b) { return $null }
        return [int]$b.EstimatedChargeRemaining
    }
    catch { return $null }
}

$battery = Get-BatteryPercent
if ($null -eq $battery) {
    Write-Host "Battery: none/desktop - physical SetSuspendState skipped"
}
elseif ($battery -lt 30) {
    Write-Host "Battery: $battery% (<30) - SKIP physical SetSuspendState"
}
else {
    Write-Host "Battery: $battery% - physical SetSuspendState not invoked by this script (safety); use OS sleep manually if needed"
}

$powerLogCandidates = @(
    (Join-Path $env:APPDATA "MugoByte\Pulse\power.log"),
    (Join-Path $env:LOCALAPPDATA "MugoByte\Pulse\power.log"),
    (Join-Path $root "power.log")
)
$powerLog = $powerLogCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

$testsOk = $false
Write-Host "Running PowerResilience tests..."
dotnet test (Join-Path $root "Pulse.Tests\Pulse.Tests.csproj") -c Release --filter "FullyQualifiedName~PowerResilience" --nologo
if ($LASTEXITCODE -eq 0) {
    $testsOk = $true
    Write-Host "PowerResilienceTests: PASS"
}
else {
    Write-Host "PowerResilienceTests: FAIL (exit $LASTEXITCODE)"
}

if ($powerLog) {
    Write-Host "power.log: found at $powerLog"
}
else {
    Write-Host "power.log: not present yet (OK if tests covered SimulateTransition)"
}

if (-not $testsOk) {
    Write-Host "FAIL: Simulate path not verified"
    exit 1
}

Write-Host "PASS: Simulate (PowerResilience SimulateTransition x20 via tests)"
exit 0
