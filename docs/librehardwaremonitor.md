# LibreHardwareMonitor (optional)

Pulse references `LibreHardwareMonitorLib` 0.9.4 for CPU Package / Tctl temperature when WMI thermal zones fail.

If `dotnet restore` fails on that package, rebuild with:

```powershell
dotnet build -c Release -p:PulseDisableLhm=true
```

WMI zone ranking still ships (PACKAGE/PKG/CPU > TZ00 > other) with clearer `LastTemperatureSource` labels such as `ACPI-TZ00 (thermal zone; not CPU package)`.
