# Pulse — Production Edition

**Canonical definition of done** for commercial deployment under MugoByte Technologies.

Product: Pulse · Platform: Windows 10 22H2+ / Windows 11 · Stack: .NET 8 + WPF (MVVM + DI)

This document supersedes informal phase language. Phases are milestones toward this production standard.

## Mission

Pulse is a commercial-grade Windows Command Center (not a simple CPU monitor): monitoring, universal search, administration, processes, hardware, launcher, maintenance, performance, diagnostics, and productivity — production quality only. No prototype shortcuts when a scalable design fits.

## Experiences

1. Transparent overlay — frameless, always on top, low cost, live metrics, drag/lock/opacity, click opens Command Center  
2. Command Center — search-first; everything in one or two actions  

## Architecture requirements

Modular: Core, Monitoring, Search, Commands, Dashboard, Hardware, Performance, Processes, Applications, Windows, Network, Storage, Security, Maintenance, Developer, Battery, Notifications, Settings, Updater, Logging, Diagnostics.

- Dependency Injection  
- MVVM  
- Async services / interfaces  
- No tightly coupled unrelated features  

See also: [PULSE_ARCHITECTURE.md](PULSE_ARCHITECTURE.md), [PULSE_MASTER_CHECKLIST.md](PULSE_MASTER_CHECKLIST.md)

## Design

Fluent 2 · Segoe UI Variable · Fluent System Icons only · Mica / Acrylic · 8px grid · radius 16–20px · motion 150–250ms · brand `#2563EB` → `#3B82F6` · Success `#22C55E` · Warning `#F59E0B` · Danger `#EF4444` · no emojis  

## Safety

Destructive actions require confirmation. Elevation only when required. Never terminate protected Windows processes without explicit confirmation. Respect permissions; no security bypasses.

## Performance targets

Startup &lt;2s · Idle CPU &lt;1% · Memory 30–60 MB where practical · Hardware refresh ~1s · Instant search · No UI freezes  

## Packaging

Release · self-contained · offline installer · uninstaller · Desktop / Start Menu shortcuts · auto-start · version + company metadata · app icon  

## Documentation (required)

Architecture · Developer Guide · User Guide · Release Notes · Known Limitations · Troubleshooting  

## Validation loop

Build → test every feature/button/search/command/theme/module → screenshots of working states → functional demos → fix → retest. Document Windows/OEM/permission limitations with graceful handling.

## Project complete only when

Implemented features built, tested, visually verified; Release + installer succeed; overlay + command center + search work; UI polished; performance reasonably met; limitations documented; suitable for real-world MugoByte deployment.
