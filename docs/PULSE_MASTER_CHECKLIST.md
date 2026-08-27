# Pulse by MugoByte Technologies — Master Feature Checklist

Source: ChatGPT share `6a60b9c9-32e0-83ea-88cc-5041c699d714`  
Updated: 2026-07-23 · App **v1.7.0**

Legend: ✅ shipped · 🟡 partial / opens Windows tools · ⬜ later (needs sensors / cloud / OEM APIs)

Architecture: see [PULSE_ARCHITECTURE.md](PULSE_ARCHITECTURE.md) — modular core, command dispatcher, validation loop.

## Product principles (shipped)
- ✅ Search-first command center (no sidebar)
- ✅ Fluent System Icons only (no emojis)
- ✅ Modular Core + ModuleRegistry (in-process plugins)
- ✅ CommandDispatcher for all catalog/search execution
- ✅ Safety layer for destructive actions (`ConfirmDestructiveActions`, default on)
- ✅ Recent + Favorites stores · Action log
- ✅ Smart ranking (exact / pinned / frequent / recent / fuzzy)
- ✅ Universal search kinds (app, setting, process, file, folder, command, …)
- ✅ Ctrl+Space global palette (fallback Ctrl+Shift+Space)
- ✅ Health score + Diagnostics module
- ✅ Profiles (Gaming/Work/Battery/Presentation/Developer)
- ✅ NotificationCenter in-memory history · Update stub
- ✅ Design tokens (#2563EB system) · Mica attempt on shell
- ✅ Overlay + shell, MugoByte branding, dark/light follow Windows
- ✅ Catalog actions via modules + search

## 1. Floating overlay
- ✅ Frameless, transparent, always on top, drag, lock, opacity/size panel
- ✅ CPU %, temp, RAM %, battery + charge, optional fan
- ✅ Click → Open Pulse · Double-click → adjust panel
- 🟡 GPU / clock / history / SMART / net speeds — not in overlay yet

## 2. Search
- ✅ Fuzzy search · settings · tools · apps (Start Menu) · commands · categories
- ✅ Keyboard: type, ↑↓, Enter, Esc, Ctrl+K
- ✅ Natural aliases (bluetooth, dark mode, clear temp, restart explorer)
- ⬜ Files/folders deep index · search history persistence

## 3. Dashboard
- ✅ Live cards: CPU, Temp, RAM, Storage, Battery, Network
- ✅ Quick actions row (performance, theme, cleanup, security, network, tools)
- 🟡 Health score / GPU card / activity feed — later

## 4–14. Category coverage (all accessible in-app)
| Category | Status |
|---|---|
| Performance | ✅ power plans (Ultimate/High/Balanced/Saver), Task Mgr, ResMon, PerfMon, sleep, startup, GPU prefs |
| Hardware | ✅ live summary + Device Mgr, msinfo, dxdiag, battery report, sound/display/BT |
| Network | ✅ Wi-Fi, Ethernet, BT, VPN, hotspot, proxy, firewall, flush DNS, renew IP, winsock reset, diagnostics, ncpa |
| Maintenance | ✅ cleanup, temp, prefetch, thumbs, recycle, clipboard, Storage Sense, optimize, chkdsk, SFC, DISM, restore point, explorer, WU |
| Applications | ✅ apps/startup/defaults + process manager End / Kill tree (no prompt) |
| Security | ✅ Defender, firewall, BitLocker, credentials, Hello, privacy, secpol, restore |
| Storage | ✅ usage lines + diskmgmt, cleanup, optimize, chkdsk |
| Developer | ✅ Terminal, PS, CMD, env, hosts, services, regedit, God Mode, WSL, netstat |
| Windows | ✅ full MMC/settings set (gpedit, taskschd, eventvwr, etc.) |
| Settings | ✅ dark/light, personalization, overlay panel, about + many ms-settings pages |

## 15–21. Later polish
- 🟡 Themes: follows Windows dark/light (OLED/Mica/custom accents later)
- ⬜ Smart toasts · Favorites · Recent history · custom hotkeys registry
- ✅ Premium shell UI: gradient canvas, accent glow search, elevated cards, chips, status bar

## How to run
- Dev: `publish\Pulse.exe --shell --dev`
- Installer: `setup\Pulse-Setup.exe` → installs to `%LocalAppData%\MugoByte\Pulse\`
- Note: `AdminLauncher.TryRelaunchElevated` exists but is not called on startup (elevation is manual / as needed)
