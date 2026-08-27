# Pulse by MugoByte Technologies

Production Windows command center: transparent overlay + search-first shell, integrated with the **MugoByte Account** ecosystem.

**Definition of done:** [docs/PULSE_PRODUCTION_MASTER.md](docs/PULSE_PRODUCTION_MASTER.md)  
**Account / licensing:** [docs/MUGOBYTE_PLATFORM.md](docs/MUGOBYTE_PLATFORM.md)

## Install

```powershell
.\build-setup.ps1
.\setup\Pulse-Setup.exe
```

Installs to `%LocalAppData%\MugoByte\Pulse\`. Activation and settings live under `%AppData%\MugoByte\Pulse\` (preserved across upgrades).

## Account

On first launch, **Sign In & Activate** with your MugoByte Account — the same hybrid flow as **MBT POS** (login → silent seat claim; license key only as fallback).

```powershell
# Local mock portal (same Sign In → auto-claim process)
$env:MBT_PLATFORM_MODE='mock'
.\publish\Pulse.exe --dev --shell --mock-account

# Skip account gate (dev)
.\publish\Pulse.exe --dev --skip-account --shell
```

## Docs

| Doc | Purpose |
|---|---|
| [USER_GUIDE.md](docs/USER_GUIDE.md) | End users |
| [DEVELOPER_GUIDE.md](docs/DEVELOPER_GUIDE.md) | Contributors |
| [MUGOBYTE_PLATFORM.md](docs/MUGOBYTE_PLATFORM.md) | Shared account SDK |
| [PULSE_ARCHITECTURE.md](docs/PULSE_ARCHITECTURE.md) | Modular architecture |
| [KNOWN_LIMITATIONS.md](docs/KNOWN_LIMITATIONS.md) | API / OEM gaps |
| [TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) | Support |
| [RELEASE_NOTES.md](docs/RELEASE_NOTES.md) | Versions |

## Dev

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish
.\publish\Pulse.exe --shell --dev --mock-account
```

Hotkeys: **Ctrl+Space** (palette), **Ctrl+K** (search when shell open).
