# Pulse — Troubleshooting

## Pulse does not stay open after Setup

1. Check `%AppData%\MugoByte\Pulse\startup.log`  
2. Confirm install exists: `%LocalAppData%\MugoByte\Pulse\Pulse.exe`  
3. Kill orphaned `Pulse` processes in Task Manager, then re-run Setup  
4. Avoid forcing UAC elevation on every start (breaks overlay/mutex)

## Overlay missing metrics (`--`)

- Temperature/fan depend on ACPI WMI — common on some laptops/desktops to be unavailable  
- CPU shows `0%` for the first sample by design  

## Search finds nothing

- Wait a second after open (Start Menu `.lnk` index)  
- Try exact names: `Task Manager`, `bluetooth`, `dark mode`  

## Destructive action did nothing

- Confirmation may have been cancelled  
- Admin-required tools need an elevated session — accept the continue prompt or run Pulse elevated once  

## Ctrl+Space does nothing

- Another app may own the hotkey — Pulse falls back to **Ctrl+Shift+Space**  
- Check `startup.log` for `hotkey registered=`  

## High memory

- Self-contained single-file bundles the runtime  
- For lighter RAM in development, use framework-dependent publish + shared .NET 8 Desktop Runtime  

## Uninstall

Start Menu → MugoByte → Uninstall Pulse  
Or: `%LocalAppData%\MugoByte\Pulse\Uninstall-Pulse.ps1`

## Logs to send for support

- `startup.log`, `error.log`, `actions.log` under `%AppData%\MugoByte\Pulse`  
- Do not send files that may contain personal document paths from search experiments  
