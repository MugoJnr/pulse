# Pulse — Context Briefing for Claude

**Audience:** Claude (or any AI) continuing work on this product.  
**Company:** MugoByte Technologies  
**Product:** Pulse (Windows desktop)  
**Repo path (typical):** `C:\Users\mugoj\Desktop\CpuTempWidget`  
**Current version:** 1.9.0  
**Stack:** .NET 8 + WPF · MVVM foundation + DI · namespace still `CpuTempWidget`, assembly name `Pulse`

---

## What Pulse is

Pulse is a **commercial Windows Command Center**, not a simple CPU widget.

It combines ideas from PowerToys, Raycast, Windows 11, Task Manager, and Everything Search into one Fluent-styled app:

1. **Transparent overlay** — always-on-top, low-cost live metrics  
2. **Command Center** — search-first shell; almost everything is one or two actions away  

Brand: professional, minimal, premium. Company: **MugoByte Technologies**.  
Sister product: **MBT POS** (Python/PyQt). Shared identity: **MugoByte Account** on `https://portal.mugobyte.com`.

Design bar: “feels like software Microsoft could have built” — Fluent 2, Segoe UI Variable, Fluent System Icons only, Mica/acrylic where possible, 8px grid, primary `#2563EB`.

---

## Two experiences

### 1. Overlay (`MainWindow`)
- Borderless / transparent / always on top  
- Shows: CPU %, CPU temp (when WMI/ACPI available), RAM, battery (+ charging), optional fan  
- Dynamic color (green / yellow / red) by load/heat  
- Drag, lock position, remember position, opacity, auto-start  
- **Left-click** → open Command Center  
- **Right-click** → Pulse menu  
- **Double-click** → adjust panel (size/opacity; auto-hide)

Metrics: `Services/SystemMonitor.cs` (Win32 + WMI). **No LibreHardwareMonitor.**

### 2. Command Center (`PulseMainWindow`)
- Search bar is the heart of navigation  
- Hotkeys: **Ctrl+Space** (global palette), **Ctrl+K** (focus search when shell open)  
- Category chips: Dashboard, Account, Favorites, Recent, modules (Performance, Hardware, Network, Maintenance, Applications, Security, Storage, Developer, Windows, Settings, Battery), Diagnostics  
- Live dashboard cards + quick actions + process manager  

---

## Feature map (what users can do)

| Area | Capabilities |
|------|----------------|
| **Universal search** | Fuzzy search across Windows Settings, apps, processes, shallow files, Pulse commands, MMC tools; ranking; recent/favorites; keyboard nav; secondary actions (pin/copy) |
| **Dashboard** | Live CPU/RAM/temp/storage/battery/network + health score + quick actions |
| **Quick actions** | Task Manager, Device Manager, Services, Terminal/PowerShell/CMD, themes, power plans, Windows Security, cleanup entry points, etc. |
| **Process manager** | List processes; End / Kill tree with **ProcessProtection** for critical Windows processes |
| **Modules** | Catalog-driven actions per category (maintenance, network, security, developer, …) via `PulseCatalog` + `ModuleRegistry` |
| **Hardware / storage** | Best-effort WMI/DriveInfo; graceful `--` when sensors missing |
| **Maintenance** | Cleanup shortcuts, SFC/DISM entry points, DNS flush, etc. — **destructive actions confirm** by default |
| **Account** | Sign in, license status, refresh, portal links (devices/billing/downloads/support), sign out |
| **Themes** | Follow Windows / dark-light helpers; design tokens in `Core/DesignTokens.cs` |
| **Notifications** | In-app notification center for high temp, low RAM/disk/battery, offline |
| **Logging** | `startup.log`, `error.log`, `actions.log`, `platform.log` under `%APPDATA%\MugoByte\Pulse\` — never log secrets |
| **Packaging** | `build-setup.ps1` → self-contained `setup\Pulse-Setup.exe` (~69 MB) → `%LOCALAPPDATA%\MugoByte\Pulse\` |

---

## How the command pipeline works

```
User search / chip / button
    → IPulseCommand
    → CommandDispatcher
    → SafetyService (confirm if destructive)
    → Execute action
    → ActivityStore (recent + actions.log)
```

- Modules implement `IPulseModule` and expose commands from `PulseCatalog` (or custom).  
- Safety default: `ConfirmDestructiveActions = true`.  
- Protected processes: `Core/ProcessProtection.cs` — extra warning before End/Kill tree.

---

## Architecture (code)

```
CpuTempWidget/                 # WPF app (Pulse.exe)
├── App.xaml.cs                # Startup: bootstrap → single-instance → AppHost DI → account gate → overlay
├── MainWindow*                # Overlay
├── PulseMainWindow*           # Command Center
├── AccountGateWindow*         # Sign In & Activate (POS-style)
├── AccountReconnectWindow*    # Soft-lock reconnect (no Continue bypass)
├── Core/                      # Commands, search, safety, health, hotkeys, modules, process protection
├── Services/                  # Monitor, catalog, bootstrap, theme, shortcuts, account bootstrap, updates
├── ViewModels/AppHost.cs      # DI composition root
├── Models/AppSettings.cs
└── MugoByte.Platform/         # Shared account/licensing/update client library
```

**Install / data paths**
- Binary: `%LOCALAPPDATA%\MugoByte\Pulse\Pulse.exe`  
- Settings/logs/secure: `%APPDATA%\MugoByte\Pulse\`  
- Secure activation/session: `%APPDATA%\MugoByte\Pulse\secure\` (DPAPI)  
- Bootstrap: Setup copies into AppData, relaunches installed exe, exits (do not let Setup own the single-instance mutex)

**Flags / env**
- `--dev` / `PULSE_SKIP_BOOTSTRAP=1` — skip AppData install handoff  
- `--shell` / `--open` — open Command Center  
- `--skip-account` — bypass account gate (dev)  
- `--mock-account` / `MBT_PLATFORM_MODE=mock` — mock portal, **same** login→claim process  
- `MBT_PORTAL_URL` — override portal base  
- `MBT_LICENSE_OFFLINE_GRACE_DAYS` — override grace (default **7**, same as MBT POS)

---

## Licensing & account (critical — matches MBT POS)

Pulse is a first-class portal product: **`product_id = pulse`**.

Portal: `https://portal.mugobyte.com`  
Catalog sections (separate): **Point of Sale** (MBT POS) · **Desktop utilities** (Pulse) · …

### User flow (production)
1. **Sign In & Activate** with MugoByte Account  
2. Client **silently claims** a Pulse seat (`POST /api/cloud/licenses/claim`)  
3. **MBT-… license key** only if no seat / claim failed  
4. Activation token signed + bound to **device fingerprint** (multi-signal SHA-256; raw HW IDs not stored)  
5. Stored via **Windows DPAPI**  
6. Offline for **7 days** after last successful cloud validate  
7. After grace: **soft-lock** — token kept, use blocked until reconnect (no “Continue anyway”)  
8. Background sync: local ~5 min, cloud ~15 min  

**There is no production “demo activation” button.** Mock is for local testing only and still does Sign In → auto-claim.

### “Logged in” meaning
- **Signed in** = DPAPI `auth_session` with access token  
- **Activated / ready** = DPAPI `activation` token valid for this machine  
- Startup gate uses **LicenseGuard** on activation — not a live portal “is logged in elsewhere” watcher  

### Shared library
`MugoByte.Platform` — intended reuse for Pulse, MBT POS (.NET port later), ExamHub, etc.  
DI: `services.AddMugoBytePlatform(PlatformOptions.ForPulse(version))`

---

## Portal / ecosystem notes

- Same account as MBT POS / Workspace  
- Licenses are **product-scoped** (`pulse` ≠ `mbt-pos` seats)  
- Workspace **Licenses** page (`/license`) shows seats with filters All / MBT POS / Pulse  
- User may have **multiple orgs** both named “My Business” — Pulse seats may be on a secondary org; switch business in workspace switcher  
- Owners need org owner/manager access to view licenses (portal API was fixed for JWT `member` + org admin membership)

---

## Safety & product rules (do not violate)

- Never terminate protected Windows processes without strong confirmation  
- Never run Shutdown/Restart PC during agent testing unless user explicitly wants a real reboot test  
- Destructive maintenance requires confirmation  
- Elevation only when required (`asInvoker` by default; no startup auto-elevate)  
- Do not log passwords or tokens  
- Prefer production-quality architecture over prototypes  
- Definition of done: `docs/PULSE_PRODUCTION_MASTER.md` + checklist — phases are milestones, not “done”

---

## Known limitations (honest)

- No deep GPU / SMART / network Mbps without extra APIs (show graceful fallbacks)  
- File search is shallow (Desktop/Documents/Downloads), not full-disk Everything-class yet  
- MVVM is foundational; much of the shell is still code-behind  
- Self-contained single-file memory is higher than the 30–60 MB aspirational target  
- Feature entitlement flags exist on claims; UI gating by `HasFeature` is not fully wired everywhere  
- Update feed works when portal publishes Pulse updates with checksum  

See `docs/KNOWN_LIMITATIONS.md`.

---

## How to build / run

```powershell
cd C:\Users\mugoj\Desktop\CpuTempWidget
dotnet build -c Release
.\build-setup.ps1          # → setup\Pulse-Setup.exe
.\setup\Pulse-Setup.exe    # install + launch

# Dev without reinstall
$env:PULSE_SKIP_BOOTSTRAP='1'
.\bin\Release\net8.0-windows\Pulse.exe --dev --shell
```

---

## Docs map

| File | Purpose |
|------|---------|
| `docs/PULSE_PRODUCTION_MASTER.md` | Commercial DoD |
| `docs/PULSE_ARCHITECTURE.md` | Modular architecture |
| `docs/PULSE_MASTER_CHECKLIST.md` | Feature inventory |
| `docs/MUGOBYTE_PLATFORM.md` | Account/licensing SDK |
| `docs/USER_GUIDE.md` / `DEVELOPER_GUIDE.md` | Humans |
| `docs/RELEASE_NOTES.md` | Version history |
| `docs/TROUBLESHOOTING.md` | Support |

---

## Related products / paths

- MBT POS source: `C:\Users\mugoj\OneDrive\Desktop\MBT POS\extracted\mbt_pos`  
- Portal: `https://portal.mugobyte.com` (Fly app `mbt-portal`)  
- Chrome automation profile for eugene: CDP launcher under POS `scripts/open_chrome_eugene.bat`

---

## One-sentence summary

**Pulse is MugoByte’s .NET 8 WPF command center: a live system overlay plus a search-first admin shell, licensed through the same MugoByte Portal account flow as MBT POS (`product_id=pulse`), with modular commands, safety confirms, and a shared `MugoByte.Platform` client for auth, device-bound activation, offline grace, and updates.**
