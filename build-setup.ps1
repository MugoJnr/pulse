#Requires -Version 5.1
$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$outDir = Join-Path $root "setup"
$setupExe = Join-Path $outDir "Pulse-Setup.exe"

Write-Host "Building Pulse-Setup.exe (self-contained .NET 8, win-x64)..." -ForegroundColor Cyan

Get-Process Pulse, Pulse-Setup, CpuTempWidget -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

if (Test-Path $setupExe) {
    try { Remove-Item $setupExe -Force -ErrorAction Stop }
    catch {
        $bak = "$setupExe.bak"
        try { Move-Item $setupExe $bak -Force; Remove-Item $bak -Force -ErrorAction SilentlyContinue } catch {}
    }
}

New-Item -ItemType Directory -Path $outDir -Force | Out-Null

Push-Location $root
try {
    $publishTmp = Join-Path $outDir "_publish_tmp"
    if (Test-Path $publishTmp) { Remove-Item $publishTmp -Recurse -Force }
    New-Item -ItemType Directory -Path $publishTmp -Force | Out-Null

    dotnet publish (Join-Path $root "CpuTempWidget.csproj") -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:IncludeAllContentForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none `
        -o $publishTmp 2>&1

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

    $published = Join-Path $publishTmp "Pulse.exe"
    if (-not (Test-Path $published)) { throw "Expected publish output not found: $published" }

    Copy-Item -Path $published -Destination $setupExe -Force
    Remove-Item $publishTmp -Recurse -Force -ErrorAction SilentlyContinue
    Get-ChildItem $outDir -Exclude "Pulse-Setup.exe" | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}
finally { Pop-Location }

$finalMb = [math]::Round((Get-Item $setupExe).Length / 1MB, 2)
if ($finalMb -lt 40) {
    Write-Warning "Setup exe is only ${finalMb} MB - .NET may not be bundled. Expected ~60+ MB."
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  $setupExe"
Write-Host "  Size: ${finalMb} MB"
Write-Host "  .NET 8 Desktop Runtime is bundled - works offline, no separate install."
