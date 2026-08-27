# LibreHardwareMonitor (optional)

Pulse references `LibreHardwareMonitorLib` 0.9.4 for CPU Package / Tctl temperature when WMI thermal zones fail.

## Initialization

- `Computer` opens with **IsCpuEnabled** and **IsMotherboardEnabled** (Lenovo often exposes package temps under motherboard/SubHardware).
- Prefer **Accept(UpdateVisitor)** when present; otherwise recurse **Update** on Hardware + SubHardware.
- Sensor preference: Package → Tctl → CCD → Core Max → CPU-named → first CPU/MB temp.
- Sensors with null values (common without Ring0/admin on Intel) are logged in `temp.log` inventory.
- Soft path: one-time elevated `--lhm-sensor-probe` writes `%APPDATA%\MugoByte\Pulse\lhm-live.json`; the main process reads it when live LHM values are empty.

If `dotnet restore` fails on that package, rebuild with:

```powershell
dotnet build -c Release -p:PulseDisableLhm=true
```

WMI zone ranking still ships (PACKAGE/PKG/CPU > TZ00 > other) with clearer `LastTemperatureSource` labels such as `ACPI-TZ00 (thermal zone; not CPU package)`.
