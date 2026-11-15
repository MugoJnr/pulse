# Pulse — Developer Guide

## Stack

- .NET 8 WPF (`net8.0-windows`)
- CommunityToolkit.Mvvm + Microsoft.Extensions.DependencyInjection
- Modular Core (`Core/`) + catalog modules (`ModuleRegistry`)
- Sensors via Win32 / WMI (`SystemMonitor`) — no LibreHardwareMonitor

## Solution layout

| Path | Role |
|---|---|
| `App.xaml.cs` | Startup, bootstrap, account gate, hotkeys, `AppHost.Build()` |
| `ViewModels/` | DI composition + view models |
| `MugoByte.Platform/` | Shared account, licensing, fingerprint, updates (multi-product) |
| `Core/` | Commands, dispatcher, search, safety, health, hotkeys, process protection |
| `Services/` | Bootstrap, catalog, monitor, theme, shortcuts, account bootstrap |
| `AccountGateWindow` | Sign In & Activate (POS claim) / create / license key fallback |
| `MainWindow` | Overlay |
| `PulseMainWindow` | Command Center shell |
| `docs/MUGOBYTE_PLATFORM.md` | Portal contracts + mock mode |
| `docs/PULSE_PRODUCTION_MASTER.md` | Production DoD |

## Build

```powershell
dotnet build -c Release
.\build-setup.ps1   # → setup\Pulse-Setup.exe (~69 MB self-contained)
```

## Publishing a release

Fleet PCs check **MugoJnr/pulse-releases** (public, `Pulse-Setup.exe` only) and fall back to **MugoJnr/pulse**. Both get the same installer until every PC is running a build that knows about `pulse-releases`. After `pulse` is private, unauthenticated PCs can only reach `pulse-releases`.

Installed copies skip a release that is not newer than the version they already run. Do not reuse 1.12.4 for the transition build.

1. Bump `<Version>` in `CpuTempWidget.csproj` to the new `X.Y.Z` and merge to `master`.
2. One-time: create a fine-grained PAT for **MugoJnr/pulse-releases** only, permission **Contents: Read and write**. Add it as the Actions secret `RELEASES_REPO_TOKEN` on **MugoJnr/pulse**. Do not commit the token. Fine-grained PATs expire — renew the secret before they do.
3. Tag and push. The tag message is the release notes.

```powershell
git tag -a vX.Y.Z -m "Release notes shown in the updater."
git push origin vX.Y.Z
```

`.github/workflows/release.yml` tests, builds `Pulse-Setup.exe`, publishes it on this repo, and mirrors the exe and notes to `pulse-releases`. The mirror never uploads source. The job fails if `RELEASES_REPO_TOKEN` is missing.

The same script can be run locally after `.\build-setup.ps1` (tokens stay in the environment, not in the command):

```powershell
$env:RELEASES_REPO_TOKEN = "<pat>"
.\scripts\publish-release.ps1 -Tag vX.Y.Z -Setup .\setup\Pulse-Setup.exe -Notes "Release notes"
```

`MBT_GITHUB_TOKEN` / `GH_TOKEN` still authenticate the legacy private-repo check and download. Leave `MBT_GITHUB_REPO` unset on fleet PCs so they use the two-repo feed.

Dev (skip AppData install):

```powershell
$env:PULSE_SKIP_BOOTSTRAP=1
.\bin\Release\net8.0-windows\Pulse.exe --shell --dev --mock-account
```

Flags: `--dev` (skip bootstrap), `--mock-account` / `MBT_PLATFORM_MODE=mock`, `--skip-account` (bypass gate).

## Adding a module

1. Register commands in `PulseCatalog` (or a dedicated module class implementing `IPulseModule`)
2. Register in `ModuleRegistry.Build()`
3. Commands execute only through `CommandDispatcher.Execute`

## DI

```csharp
AppHost.Build();
var dash = AppHost.Get<DashboardViewModel>();
```

Prefer injecting `ISystemMonitor` into new view models instead of `new SystemMonitor()`.

## Coding standards

- Fluent icons only · DesignTokens for brand colors  
- Destructive → `IsDestructive` + safety confirm  
- Protected processes → `ProcessProtection`  
- Log significant actions via `ActivityStore`  
- No prototype hacks when a service/interface fits  

## Canonical docs

1. [PULSE_PRODUCTION_MASTER.md](PULSE_PRODUCTION_MASTER.md) — DoD  
2. [PULSE_ARCHITECTURE.md](PULSE_ARCHITECTURE.md) — modular vision  
3. [PULSE_MASTER_CHECKLIST.md](PULSE_MASTER_CHECKLIST.md) — feature inventory  
4. [KNOWN_LIMITATIONS.md](KNOWN_LIMITATIONS.md)  
