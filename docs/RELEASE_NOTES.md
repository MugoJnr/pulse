# Pulse — Release Notes

## 1.9.1 — Production depth (2026-07-26)

### Monitoring
- Network throughput (Mbps/Kbps) via interface byte deltas
- GPU name (WMI) + best-effort GPU engine load %
- Dashboard GPU card; hardware/diagnostics show GPU/network
- Health score includes GPU load when available

### Process manager
- Filter, headers, End / Kill tree / Suspend / Resume / High / Locate
- Destructive confirms; danger styling on Kill tree

### Search & alerts
- Depth-3 file search + persisted search history
- Persisted notifications with cooldown; Alerts chip + dashboard strip

### Performance
- Startup apps list (HKCU Run) with remove confirm

### Installer
- Bootstrap refreshes when **FileVersion** changes (not only size/mtime)

## 1.8.0 — Production Edition foundation
DI, protected processes, shortcuts, docs suite.
