# Pulse — User Guide

**Pulse** by MugoByte Technologies is a Windows command center: a transparent always-on-top overlay plus a search-first Command Center.

## Install

1. Run `setup\Pulse-Setup.exe`
2. Pulse installs to `%LocalAppData%\MugoByte\Pulse\Pulse.exe`
3. Desktop and Start Menu shortcuts are created
4. Optional: enable **Start with Windows** from the overlay menu or first-run welcome

## Overlay

- Shows CPU, temperature (when available), RAM, battery
- **Left-click** — open Command Center  
- **Double-click** — size and opacity  
- **Right-click** — menu (startup, lock, restart Pulse, about)  
- Drag to move; Lock to freeze position  

## Command Center

- **Ctrl+Space** — open Pulse and focus search (anywhere)  
- **Ctrl+K** — focus search when the shell is open  
- Type to search settings, apps, tools, processes, files (depth-3 under common folders)  
- Enter — run · Right-click result — Pin / Copy  
- Category chips: Dashboard, Account, **Alerts**, Favorites, Recent, modules, Diagnostics  

## Live metrics

Dashboard cards: CPU, temperature, memory, **GPU**, storage, battery, **network throughput**.  
Process manager: filter, End / Kill tree / Suspend / Resume / Priority / Locate.  
Performance: power tools + **startup apps** (user Run key).

## MugoByte Account

Pulse uses the **same account onboarding as MBT POS**.

First launch asks you to **Sign In & Activate** with your MugoByte Account (shared with MBT POS and the Portal).

- If a license seat is available, this PC is activated automatically — no key needed  
- Paste an online **MBT-…** license key only if no seat is available  
- Activation is bound to this PC and stored securely (Windows DPAPI)  
- Works offline for up to **7 days** after the last successful online check (same default as MBT POS; configurable)  
- When grace ends, Pulse soft-locks (activation kept; reconnect required) — matching POS offline lock  
- **Account** chip: profile, license, refresh, portal links (devices, billing, downloads, support), sign out  

For local development without the live portal, run with `--mock-account` and still use **Sign In & Activate** (mock auto-claims a seat).

Upgrades keep your activation and settings under `%AppData%\MugoByte\Pulse\`.

## Safety

Destructive actions (cleanup, kill tree, SFC/DISM, etc.) ask for confirmation by default.  
Toggle: Settings → **Confirm destructive actions**.  
Protected Windows processes require an extra warning before End.

## Uninstall

Start Menu → MugoByte → **Uninstall Pulse**, or run:

`%LocalAppData%\MugoByte\Pulse\Uninstall-Pulse.ps1`

Settings under `%AppData%\MugoByte\Pulse` are kept unless you delete them manually.

## Support paths

- Logs: `%AppData%\MugoByte\Pulse\startup.log`, `error.log`, `actions.log`, `platform.log`  
- Settings: `%AppData%\MugoByte\Pulse\settings.json`  
- Secure activation: `%AppData%\MugoByte\Pulse\secure\` (do not copy between PCs)
