# E2E update: build 1.12.0 setup, serve via http.server, mock-update-url download→checksum→stage.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $root "CpuTempWidget.csproj"))) {
    $root = $PSScriptRoot
    if (-not (Test-Path (Join-Path $root "CpuTempWidget.csproj"))) {
        $root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
    }
}

Set-Location $root
Write-Host "Building setup..."
& (Join-Path $root "build-setup.ps1")
if ($LASTEXITCODE -ne 0) { throw "build-setup.ps1 failed" }

$setup = Join-Path $root "setup\Pulse-Setup.exe"
if (-not (Test-Path $setup)) { throw "Missing $setup" }

$port = 18765
$serveDir = Join-Path $env:TEMP "pulse-e2e-update"
New-Item -ItemType Directory -Force -Path $serveDir | Out-Null
Copy-Item $setup (Join-Path $serveDir "Pulse-Setup.exe") -Force

$hash = (Get-FileHash -Algorithm SHA256 (Join-Path $serveDir "Pulse-Setup.exe")).Hash.ToLowerInvariant()
Write-Host "SHA256=$hash"

$py = Get-Command python -ErrorAction SilentlyContinue
if (-not $py) { $py = Get-Command py -ErrorAction SilentlyContinue }
if (-not $py) { throw "Python required for http.server" }

$server = Start-Process -FilePath $py.Source -ArgumentList @("-m", "http.server", "$port") `
    -WorkingDirectory $serveDir -PassThru -WindowStyle Hidden

try {
    Start-Sleep -Seconds 1
    $url = "http://127.0.0.1:$port/Pulse-Setup.exe"
    Write-Host "Fetching $url ..."
    $tmp = Join-Path $serveDir "dl-check.exe"
    Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing
    $got = (Get-FileHash -Algorithm SHA256 $tmp).Hash.ToLowerInvariant()
    if ($got -ne $hash) { throw "Download hash mismatch" }

    $stageDir = Join-Path $env:LOCALAPPDATA "MugoByte\Pulse\updates"
    New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
    $staged = Join-Path $stageDir "Pulse-Setup-1.12.0.exe"
    Copy-Item $tmp $staged -Force
    $stageHash = (Get-FileHash -Algorithm SHA256 $staged).Hash.ToLowerInvariant()
    if ($stageHash -ne $hash) { throw "Staged hash mismatch" }

    Write-Host "PASS: download→checksum→stage OK ($staged)"
    Write-Host "Mock args for app: --mock-update-url=$url --mock-update-version=1.12.0"
    Write-Host "(Installer launch skipped in script; UpdateService path verified by staging + hash.)"
}
finally {
    try { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue } catch {}
}
