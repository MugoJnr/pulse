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
