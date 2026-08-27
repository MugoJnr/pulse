# Pulse Architecture (MugoByte Technologies)

Definition of done = this document + [PULSE_MASTER_CHECKLIST.md](PULSE_MASTER_CHECKLIST.md).

Phases are milestones toward the complete vision. Do not treat Phase 1 as complete.

## Validation loop

Do not stop after implementing a phase. Continue validating every feature in the master checklist. Build each module iteratively, test it, capture screenshots of working functionality, compare against the specification, fix deviations, and repeat until the implementation matches the complete design. Treat the checklist as the definition of done. Do not mark the project complete while required checklist items remain unimplemented unless they are impossible due to Windows API, hardware, or permission limitations — document the limitation and implement graceful handling.

## Target layout

```
Pulse
├── Core
│   ├── Search Engine
│   ├── Command Dispatcher
│   ├── Theme Engine / Design Tokens
│   ├── Settings
│   ├── Notifications
│   ├── Recent / Favorites
│   ├── Safety Layer
│   ├── Action Log
│   └── Module / Plugin Registry
│
└── Modules
    ├── Dashboard
    ├── Hardware
    ├── Performance
    ├── Applications
    ├── Processes
    ├── Windows
    ├── Network
    ├── Security
    ├── Storage
    ├── Maintenance
    ├── Battery
    └── Developer
```

## Command path

Search → Command → Dispatcher → Safety (if needed) → Execute → Log + Recent

## Safety (default on for destructive)

Confirm: Shut down/Restart PC, Kill tree, Empty Recycle Bin, Winsock reset, SFC/DISM, Clear Prefetch.  
Setting: `ConfirmDestructiveActions` (default true). Non-destructive actions stay one-click.

## Design tokens

| Token | Value |
|---|---|
| Primary | `#2563EB` |
| Gradient | `#2563EB` → `#3B82F6` |
| Success | `#22C55E` |
| Warning | `#F59E0B` |
| Danger | `#EF4444` |
| Radius | 18px |
| Card padding | 20px |
| Motion | 180ms |
| Typography | Segoe UI Variable |
| Icons | Fluent System Icons only |

## Deferred / limitations

- Full-disk file index
- External plugin DLLs (registry is in-process for now)
- GPU / SMART / net Mbps without additional APIs
- CDN auto-updates until a feed URL is configured
