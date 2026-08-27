# Pulse — Known Limitations

| Area | Limitation | Handling |
|---|---|---|
| GPU metrics | Best-effort WMI name + GPU Engine counters | Shows `--` / name-only when counters unavailable |
| SMART / disk temp | Needs storage APIs / OEM | Storage module uses DriveInfo usage + Disk Management |
| Network Mbps | Sampled from NIC byte deltas | Requires 1–2 refresh ticks before first Mbps value |
| Temperature / fan | WMI ACPI only | Show `--` / hide fan when unavailable |
| Full-disk search | Not Everything-class | Depth-3 BFS under Desktop/Docs/Downloads/Pictures |
| External plugins | In-process modules only | `ModuleRegistry` ready for future DLL loader |
| Auto-update CDN | Feed URL not configured | Settings shows stub message |
| Mica | Win11 DWM; may no-op on older builds | Falls back to painted gradient |
| WPF Acrylic | Limited vs WinUI | Acrylic-like panel brushes |
| Dual monitors | Overlay remembers one position | Multi-monitor move supported via drag |
| Elevation | asInvoker by default | Elevation prompt when command requires admin |
| Update check 401 | Portal `/api/cloud/updates` requires auth | Pulse sends Bearer when signed in; unsigned checks are quiet (no spam) |
| Memory target 30–60 MB | Self-contained single-file often higher | Framework-dependent publish is lighter for dev |
| Pulse product on Portal | `pulse` may not be registered in production catalog yet | Use `--mock-account` + Sign In & Activate; live claim needs portal seat |
| Portal device rename UI | Managed in Portal web app | Pulse opens Portal Devices URL |
| Asymmetric token verify | Local HMAC device-bind today | Portal opaque sig rebound locally; future RSA public verify in shared SDK |

Document new limitations here when an API or OEM constraint blocks a checklist item.
