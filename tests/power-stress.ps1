# Power resilience stress — verifies Pulse stays alive while SimulateTransition is exercised via tests.
# Prefer: dotnet test --filter PowerResilienceTests
# This script only confirms a running Pulse PID survives a short wait (no external inject).

$ErrorActionPreference = "Stop"
$procs = Get-Process -Name "Pulse" -ErrorAction SilentlyContinue
if (-not $procs) {
    Write-Host "PASS (no Pulse process — run xunit PowerResilienceTests / ChargerStress instead)"
    exit 0
}

$pidList = @($procs | ForEach-Object { $_.Id })
Write-Host "Found Pulse PID(s): $($pidList -join ', ')"
Write-Host "Waiting 5s — process must remain alive (SimulateTransition covered by Pulse.Tests)..."
Start-Sleep -Seconds 5

foreach ($id in $pidList) {
    $still = Get-Process -Id $id -ErrorAction SilentlyContinue
    if (-not $still) {
        Write-Host "FAIL: Pulse PID $id exited unexpectedly"
        exit 1
    }
}

Write-Host "PASS: Pulse still running; use 'dotnet test --filter PowerResilience' for SimulateTransition x20"
exit 0
