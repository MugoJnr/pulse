using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;

namespace CpuTempWidget.Services;

public sealed class SystemReading
{
    public float CpuPercent { get; init; }
    public float? TemperatureC { get; init; }
    public float RamPercent { get; init; }
    public double RamUsedGb { get; init; }
    public double RamTotalGb { get; init; }
    public float? BatteryPercent { get; init; }
    public bool BatteryPresent { get; init; }
    public bool IsCharging { get; init; }
    public bool OnAcPower { get; init; }
    /// <summary>Charger power in watts while charging; null when not charging or unavailable.</summary>
    public double? ChargeWatts { get; init; }
    public int? FanRpm { get; init; }
    public float StoragePercent { get; init; }
    public string StorageLabel { get; init; } = "--";
    public bool NetworkOnline { get; init; }
    public string NetworkLabel { get; init; } = "Offline";
    public double? NetworkMbps { get; init; }
    public string? GpuName { get; init; }
    public float? GpuLoadPercent { get; init; }
}

/// <summary>
/// Lightweight sampler: Win32 for CPU/RAM/battery, WMI thermal + fan when exposed.
/// </summary>
public sealed class SystemMonitor : IDisposable
{
    private long _idlePrev;
    private long _kernelPrev;
    private long _userPrev;
    private bool _cpuPrimed;
    private long _netBytesPrev;
    private DateTime _netSampleUtc = DateTime.MinValue;
    private double? _cachedMbps;
    private double? _chargeWattsEma;
    private DateTime _nextChargeProbeUtc = DateTime.MinValue;
    private double? _lastChargeSample;

    // ---- Shared slow probes (thermal / fan / GPU): one background sampler for the whole app,
    // ---- so Read() never blocks the UI thread on WMI or PerformanceCounter calls.
    private static readonly object SlowGate = new();
    private static float? _sTempC;
    private static string? _sTempSource;
    private static DateTime _sTempAtUtc = DateTime.MinValue;
    private static DateTime _sTempNextUtc = DateTime.MinValue;
    private static DateTime _lastTempLogUtc = DateTime.MinValue;
    private static int _tempFailStreak;
    private static int? _sFanRpm;
    private static DateTime _sFanNextUtc = DateTime.MinValue;
    private static int _fanFailStreak;
    private static string? _sGpuName;
    private static bool _gpuNameProbed;
    private static float? _sGpuLoad;
    private static DateTime _sGpuNextUtc = DateTime.MinValue;
    private static readonly Dictionary<string, System.Diagnostics.PerformanceCounter> GpuCounters =
        new(StringComparer.OrdinalIgnoreCase);
    private static bool _gpuCountersPrimed;
    private static Thread? _slowSampler;
    private static volatile bool _slowStop;
    private static bool _slowLoopErrorLogged;

    public static string? LastTemperatureSource => _sTempSource;

    /// <summary>Starts the background thermal sampler if needed (tests / power recovery).</summary>
    public static void EnsureSampler() => EnsureSlowSampler();

    private static void EnsureSlowSampler()
    {
        if (_slowSampler is { IsAlive: true }) return;
        lock (SlowGate)
        {
            if (_slowSampler is { IsAlive: true }) return;
            _slowStop = false;
            var t = new Thread(SlowLoop)
            {
                IsBackground = true,
                Name = "Pulse.SlowSampler",
                Priority = ThreadPriority.BelowNormal
            };
            _slowSampler = t;
            t.Start();
        }
    }

    private static void SlowLoop()
    {
        while (!_slowStop)
        {
            try
            {
                var now = DateTime.UtcNow;

                if (now >= _sTempNextUtc)
                {
                    // Formatted-data WMI queries are expensive; 5s is plenty for temps.
                    var intervalSeconds = _tempFailStreak == 0 ? 5 : Math.Min(30, 4 * _tempFailStreak);
                    _sTempNextUtc = now.AddSeconds(intervalSeconds);

                    // Publish WMI thermal immediately so UI/tests are not blocked by LHM Open().
                    var wmi = TryReadPerfThermalZone(preferCpuNamed: true)
                              ?? TryReadAcpiThermalZone()
                              ?? TryReadPerfThermalZone(preferCpuNamed: false);
                    if (wmi is { } wmiHit)
                    {
                        _sTempC = wmiHit.Value;
                        _sTempSource = wmiHit.Source;
                        _sTempAtUtc = now;
                        _tempFailStreak = 0;
                    }

                    // Cached LHM upgrade when CPU Package/Tctl is available.
                    var lhm = TryReadLibreHardwareMonitorCached();
                    var reading = PreferCpuPackageReading(lhm, wmi);
                    if (reading is { } hit)
                    {
                        _sTempC = hit.Value;
                        _sTempSource = hit.Source;
                        _sTempAtUtc = now;
                        _tempFailStreak = 0;
                    }
                    else if (wmi is null)
                    {
                        _tempFailStreak = Math.Min(_tempFailStreak + 1, 6);
                        if (_tempFailStreak >= 3)
                        {
                            _sTempC = null;
                            _sTempSource = "unavailable";
                        }
                    }

                    MaybeLogTemperature(now);
                }

                if (now >= _sFanNextUtc)
                {
                    var intervalSeconds = _fanFailStreak == 0 ? 5 : Math.Min(30, 5 * _fanFailStreak);
                    _sFanNextUtc = now.AddSeconds(intervalSeconds);

                    var rpm = TryReadWin32Fan() ?? TryReadPerfFan();
                    if (rpm is > 0)
                    {
                        _sFanRpm = rpm;
                        _fanFailStreak = 0;
                    }
                    else
                    {
                        _fanFailStreak = Math.Min(_fanFailStreak + 1, 6);
                        if (_fanFailStreak >= 3)
                            _sFanRpm = null;
                    }
                }

                if (!_gpuNameProbed)
                {
                    _sGpuName = ProbeGpuName();
                    _gpuNameProbed = true;
                }

                if (now >= _sGpuNextUtc)
                {
                    _sGpuNextUtc = now.AddSeconds(3);
                    _sGpuLoad = ReadGpuEngineLoadCached();
                }
            }
            catch (Exception ex)
            {
                if (!_slowLoopErrorLogged)
                {
                    _slowLoopErrorLogged = true;
                    DiagnosticLog.WriteError("SystemMonitor.SlowLoop", ex);
                }
            }

            Thread.Sleep(500);
        }
    }

    private static void MaybeLogTemperature(DateTime nowUtc)
    {
        if (nowUtc - _lastTempLogUtc < TimeSpan.FromSeconds(30)) return;
        _lastTempLogUtc = nowUtc;
        var ageMs = _sTempAtUtc == DateTime.MinValue
            ? -1
            : (int)(nowUtc - _sTempAtUtc).TotalMilliseconds;
        var value = _sTempC is float t ? t.ToString("0.0", CultureInfo.InvariantCulture) : "null";
        DiagnosticLog.WriteTemp(
            $"temp={value}C source={_sTempSource ?? "unavailable"} ageMs={ageMs}");
    }

    /// <summary>Force re-probe after sleep / lock / display changes. Never shuts down the app.</summary>
    public static void NotifyPowerTransition(string reason)
    {
        try
        {
            lock (SlowGate)
            {
                _tempFailStreak = 0;
                _fanFailStreak = 0;
                _sTempNextUtc = DateTime.MinValue;
                _sFanNextUtc = DateTime.MinValue;
                _sGpuNextUtc = DateTime.MinValue;
                DisposeGpuCountersUnlocked();
                _gpuCountersPrimed = false;
            }

            EnsureSlowSampler();
            DiagnosticLog.WritePower($"NotifyPowerTransition:{reason}");
        }
        catch (Exception ex)
        {
            DiagnosticLog.WritePower("NotifyPowerTransition failed", ex, reason);
        }
    }

    /// <summary>Stop the shared sampler on app exit.</summary>
    public static void ShutdownSampler()
    {
        try
        {
            _slowStop = true;
            lock (SlowGate)
            {
                DisposeGpuCountersUnlocked();
                _gpuCountersPrimed = false;
            }
            CloseLibreHardwareMonitor();
            DiagnosticLog.WritePower("ShutdownSampler");
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("ShutdownSampler failed", ex);
        }
    }

    private static void DisposeGpuCountersUnlocked()
    {
        foreach (var c in GpuCounters.Values)
        {
            try { c.Dispose(); } catch { }
        }
        GpuCounters.Clear();
    }

    public SystemReading Read()
    {
        var cpu = ReadCpuPercent();
        var (ramPercent, ramUsed, ramTotal) = ReadRam();
        var (present, percent, charging, onAc) = ReadBattery();
        var chargeWatts = ReadChargeWattsSmoothed(onAc || charging);
        var temp = ReadTemperatureCached();
        var fan = ReadFanRpmCached();
        var (storagePercent, storageLabel) = ReadStorage();
        var (online, networkLabel, mbps) = ReadNetwork();
        var (gpuName, gpuLoad) = ReadGpuCached();

        return new SystemReading
        {
            CpuPercent = cpu,
            TemperatureC = temp,
            RamPercent = ramPercent,
            RamUsedGb = ramUsed,
            RamTotalGb = ramTotal,
            BatteryPercent = percent,
            BatteryPresent = present,
            IsCharging = charging,
            OnAcPower = onAc,
            ChargeWatts = chargeWatts,
            FanRpm = fan,
            StoragePercent = storagePercent,
            StorageLabel = storageLabel,
            NetworkOnline = online,
            NetworkLabel = networkLabel,
            NetworkMbps = mbps,
            GpuName = gpuName,
            GpuLoadPercent = gpuLoad
        };
    }

    private static int? ReadFanRpmCached()
    {
        EnsureSlowSampler();
        return _sFanRpm;
    }

    private static int? TryReadWin32Fan()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\CIMV2",
                "SELECT Name, DesiredSpeed, VariableSpeed, ActiveCooling FROM Win32_Fan");
            using var results = searcher.Get();

            int? best = null;
            foreach (ManagementObject obj in results)
            {
                using (obj)
                {
                    if (obj["DesiredSpeed"] is null)
                        continue;

                    var speed = Convert.ToUInt32(obj["DesiredSpeed"], CultureInfo.InvariantCulture);
                    if (!IsValidRpm(speed))
                        continue;

                    best = best.HasValue ? Math.Max(best.Value, (int)speed) : (int)speed;
                }
            }

            return best;
        }
        catch
        {
            return null;
        }
    }

    private static int? TryReadPerfFan()
    {
        // Some OEMs expose fan RPM via perf counters (class name may not exist on all PCs).
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\CIMV2",
                "SELECT Name, Frequency FROM Win32_PerfFormattedData_Counters_FanInformation");
            using var results = searcher.Get();

            int? best = null;
            foreach (ManagementObject obj in results)
            {
                using (obj)
                {
                    if (obj["Frequency"] is null)
                        continue;

                    var rpm = Convert.ToUInt32(obj["Frequency"], CultureInfo.InvariantCulture);
                    if (!IsValidRpm(rpm))
                        continue;

                    best = best.HasValue ? Math.Max(best.Value, (int)rpm) : (int)rpm;
                }
            }

            return best;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsValidRpm(uint rpm) => rpm is >= 200 and <= 20000;

    private static float? ReadTemperatureCached()
    {
        EnsureSlowSampler();
        return _sTempC;
    }

    private static TempHit? TryReadPerfThermalZone(bool preferCpuNamed)
    {
        try
        {
            // Prefer HighPrecisionTemperature (tenths of Kelvin). The coarse Temperature
            // counter is whole Kelvin and can lag; both normalize via NormalizeToCelsius.
            using var searcher = new ManagementObjectSearcher(
                @"root\CIMV2",
                "SELECT Name, Temperature, HighPrecisionTemperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");
            using var results = searcher.Get();

            var candidates = new List<(TempHit Hit, int Score, bool HighPrecision)>();

            foreach (ManagementObject obj in results)
            {
                using (obj)
                {
                    double? kelvin = null;
                    var highPrecision = false;
                    if (obj["HighPrecisionTemperature"] is not null)
                    {
                        var hp = Convert.ToDouble(obj["HighPrecisionTemperature"], CultureInfo.InvariantCulture);
                        if (hp > 0)
                        {
                            kelvin = hp / 10.0; // tenths of Kelvin → Kelvin
                            highPrecision = true;
                        }
                    }

                    if (kelvin is null && obj["Temperature"] is not null)
                        kelvin = Convert.ToDouble(obj["Temperature"], CultureInfo.InvariantCulture);

                    if (kelvin is null)
                        continue;

                    var celsius = NormalizeToCelsius(kelvin.Value);
                    if (celsius is null)
                        continue;

                    var name = obj["Name"]?.ToString() ?? string.Empty;
                    var score = ScoreThermalZoneName(name);
                    var label = FormatThermalSource("PerfThermal", name, score);
                    candidates.Add((new TempHit(celsius.Value, label), score, highPrecision));
                }
            }

            return PickBestTemp(candidates, preferCpuNamed);
        }
        catch
        {
            return null;
        }
    }

    private static TempHit? TryReadAcpiThermalZone()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI",
                "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            using var results = searcher.Get();

            var candidates = new List<(TempHit Hit, int Score, bool HighPrecision)>();

            foreach (ManagementObject obj in results)
            {
                using (obj)
                {
                    if (obj["CurrentTemperature"] is null)
                        continue;

                    var raw = Convert.ToDouble(obj["CurrentTemperature"], CultureInfo.InvariantCulture) / 10.0;
                    var celsius = NormalizeToCelsius(raw);
                    if (celsius is null)
                        continue;

                    var name = obj["InstanceName"]?.ToString() ?? string.Empty;
                    var score = ScoreThermalZoneName(name);
                    var label = FormatThermalSource("ACPI", name, score);
                    candidates.Add((new TempHit(celsius.Value, label), score, false));
                }
            }

            return PickBestTemp(candidates, preferCpuNamed: true);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Prefer LHM CPU Package/Tctl over ACPI/Perf thermal zones labeled "not CPU package".
    /// </summary>
    private static TempHit? PreferCpuPackageReading(TempHit? lhm, TempHit? wmi)
    {
        if (lhm is null) return wmi;
        if (wmi is null) return lhm;
        if (IsCpuPackageLike(lhm.Value.Source)) return lhm;
        if (IsCpuPackageLike(wmi.Value.Source)) return wmi;
        // LHM still preferred over generic thermal zones when neither is clearly package-named.
        return lhm;
    }

    private static bool IsCpuPackageLike(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        if (source.Contains("not CPU package", StringComparison.OrdinalIgnoreCase))
            return false;
        return source.Contains("Package", StringComparison.OrdinalIgnoreCase)
               || source.Contains("Tctl", StringComparison.OrdinalIgnoreCase)
               || source.Contains("LHM:", StringComparison.OrdinalIgnoreCase)
               || source.Contains("CPU Core", StringComparison.OrdinalIgnoreCase);
    }

    private static TempHit? _lhmCache;
    private static DateTime _lhmNextUtc = DateTime.MinValue;
    private static bool _lhmFailLogged;
    private static bool _lhmInventoryLogged;
    private static bool _lhmElevationQueued;
    private static readonly object LhmGate = new();
    private static object? _lhmComputer;
    private static Type? _lhmComputerType;
    private static Type? _lhmIHardware;
    private static Type? _lhmISensor;
    private static object? _lhmTempEnum;
    private static bool _lhmOpenFailed;

    /// <summary>Shared live cache written by elevated <c>--lhm-sensor-probe</c>.</summary>
    public static string LhmLiveCachePath =>
        Path.Combine(DiagnosticLog.LogDirectory, "lhm-live.json");

    /// <summary>Cached LHM probe — keeps Computer open; Update() is cheap after Open().</summary>
    private static TempHit? TryReadLibreHardwareMonitorCached()
    {
        var now = DateTime.UtcNow;
        if (now < _lhmNextUtc)
            return _lhmCache;

        _lhmNextUtc = now.AddSeconds(15);
        try
        {
            _lhmCache = TryReadLibreHardwareMonitor() ?? TryReadElevatedLhmCache();
            if (_lhmCache is null)
            {
                if (!_lhmFailLogged)
                {
                    _lhmFailLogged = true;
                    DiagnosticLog.WriteTemp(
                        AdminLauncher.IsElevated
                            ? "LHM: zero temperature values even when elevated (hardware/driver limitation)"
                            : "LHM: no usable CPU Package/Tctl/core values (Ring0 often needs elevation) - using WMI thermal fallback");
                }
                MaybeOfferLhmElevationOnce();
            }
            else if (_lhmCache is { } hit)
            {
                DiagnosticLog.WriteTemp($"LHM ok source={hit.Source} temp={hit.Value:0.0}C elevated={AdminLauncher.IsElevated}");
            }
        }
        catch (Exception ex)
        {
            _lhmCache = null;
            if (!_lhmFailLogged)
            {
                _lhmFailLogged = true;
                DiagnosticLog.WriteTemp("LHM probe failed", ex);
            }
        }

        return _lhmCache;
    }

    /// <summary>
    /// Optional LibreHardwareMonitor path for true CPU package / Tctl / core sensors.
    /// Soft-loads the assembly; Open with CPU+motherboard, Accept/Update recurse including SubHardware.
    /// </summary>
    private static TempHit? TryReadLibreHardwareMonitor()
    {
        lock (LhmGate)
        {
            if (!EnsureLibreHardwareMonitorOpen())
                return null;

            try
            {
                AcceptOrUpdateAllHardware();
                var hit = CollectBestLhmTemperature();
                if (hit is null)
                    hit = CollectBestLhmTemperature(); // second pass after Ring0 settles
                return hit;
            }
            catch
            {
                return null;
            }
        }
    }

    private static bool EnsureLibreHardwareMonitorOpen()
    {
        if (_lhmComputer is not null && _lhmComputerType is not null)
            return true;
        if (_lhmOpenFailed)
            return false;

        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name is "LibreHardwareMonitorLib");
            if (asm is null)
            {
                try { asm = System.Reflection.Assembly.Load("LibreHardwareMonitorLib"); }
                catch { /* try path below */ }
            }
            if (asm is null)
            {
                var path = Path.Combine(AppContext.BaseDirectory, "LibreHardwareMonitorLib.dll");
                if (File.Exists(path))
                    asm = System.Reflection.Assembly.LoadFrom(path);
            }
            if (asm is null)
            {
                _lhmOpenFailed = true;
                return false;
            }

            var computerType = asm.GetType("LibreHardwareMonitor.Hardware.Computer");
            var iHardware = asm.GetType("LibreHardwareMonitor.Hardware.IHardware");
            var iSensor = asm.GetType("LibreHardwareMonitor.Hardware.ISensor");
            var sensorType = asm.GetType("LibreHardwareMonitor.Hardware.SensorType");
            if (computerType is null || iHardware is null || iSensor is null || sensorType is null)
            {
                _lhmOpenFailed = true;
                return false;
            }

            var computer = Activator.CreateInstance(computerType);
            if (computer is null)
            {
                _lhmOpenFailed = true;
                return false;
            }

            computerType.GetProperty("IsCpuEnabled")?.SetValue(computer, true);
            computerType.GetProperty("IsMotherboardEnabled")?.SetValue(computer, true);
            computerType.GetMethod("Open")?.Invoke(computer, null);

            _lhmComputer = computer;
            _lhmComputerType = computerType;
            _lhmIHardware = iHardware;
            _lhmISensor = iSensor;
            _lhmTempEnum = Enum.Parse(sensorType, "Temperature");

            // Prefer IVisitor Accept when present (official UpdateVisitor pattern).
            TryAcceptUpdateVisitor(asm, computer, computerType);
            Thread.Sleep(200);
            AcceptOrUpdateAllHardware();

            DiagnosticLog.WriteTemp(
                $"LHM Open ok elevated={AdminLauncher.IsElevated} cpu+motherboard enabled");
            return true;
        }
        catch (Exception ex)
        {
            _lhmOpenFailed = true;
            DiagnosticLog.WriteTemp("LHM Open failed", ex);
            CloseLibreHardwareMonitor();
            return false;
        }
    }

    private static void TryAcceptUpdateVisitor(System.Reflection.Assembly asm, object computer, Type computerType)
    {
        try
        {
            var iVisitor = asm.GetType("LibreHardwareMonitor.Hardware.IVisitor");
            var iComputer = asm.GetType("LibreHardwareMonitor.Hardware.IComputer");
            var iHardware = asm.GetType("LibreHardwareMonitor.Hardware.IHardware");
            var iSensor = asm.GetType("LibreHardwareMonitor.Hardware.ISensor");
            var iParameter = asm.GetType("LibreHardwareMonitor.Hardware.IParameter");
            if (iVisitor is null || iComputer is null || iHardware is null || iSensor is null || iParameter is null)
                return;

            // Hand-rolled visitor via Update/SubHardware recurse is enough; Accept(null) is unsafe.
            // Call Computer.Accept only if LibreHardwareMonitor ships UpdateVisitor in this build.
            var updateVisitorType = asm.GetType("LibreHardwareMonitor.Hardware.UpdateVisitor");
            if (updateVisitorType is null) return;
            var visitor = Activator.CreateInstance(updateVisitorType);
            if (visitor is null) return;
            computerType.GetMethod("Accept", new[] { iVisitor })?.Invoke(computer, new[] { visitor });
        }
        catch
        {
            // Fall through to manual Update recurse.
        }
    }

    private static void AcceptOrUpdateAllHardware()
    {
        if (_lhmComputer is null || _lhmComputerType is null || _lhmIHardware is null)
            return;

        try
        {
            var asm = _lhmComputerType.Assembly;
            var updateVisitorType = asm.GetType("LibreHardwareMonitor.Hardware.UpdateVisitor");
            var iVisitor = asm.GetType("LibreHardwareMonitor.Hardware.IVisitor");
            if (updateVisitorType is not null && iVisitor is not null)
            {
                var visitor = Activator.CreateInstance(updateVisitorType);
                if (visitor is not null)
                {
                    _lhmComputerType.GetMethod("Accept", new[] { iVisitor })
                        ?.Invoke(_lhmComputer, new[] { visitor });
                    return;
                }
            }
        }
        catch { /* manual recurse below */ }

        if (_lhmComputerType.GetProperty("Hardware")?.GetValue(_lhmComputer) is not System.Collections.IEnumerable list)
            return;
        foreach (var hw in list)
        {
            if (hw is null) continue;
            UpdateHardwareTree(hw);
        }
    }

    private static void UpdateHardwareTree(object hw)
    {
        if (_lhmIHardware is null) return;
        try { _lhmIHardware.GetMethod("Update")?.Invoke(hw, null); } catch { }
        if (_lhmIHardware.GetProperty("SubHardware")?.GetValue(hw) is not System.Collections.IEnumerable subs)
            return;
        foreach (var sub in subs)
        {
            if (sub is null) continue;
            UpdateHardwareTree(sub);
        }
    }

    private static TempHit? CollectBestLhmTemperature()
    {
        if (_lhmComputer is null || _lhmComputerType is null || _lhmIHardware is null || _lhmISensor is null || _lhmTempEnum is null)
            return null;
        if (_lhmComputerType.GetProperty("Hardware")?.GetValue(_lhmComputer) is not System.Collections.IEnumerable list)
            return null;

        float? package = null, tctl = null, ccd = null, coreMax = null, cpuNamed = null, anyCpu = null, any = null;
        string? packageName = null, tctlName = null, ccdName = null, coreMaxName = null, cpuNamedName = null, anyCpuName = null, anyName = null;
        var inventory = new List<string>();
        var nullCount = 0;
        var valueCount = 0;

        void Consider(object hw, bool underCpu)
        {
            var hwType = _lhmIHardware!.GetProperty("HardwareType")?.GetValue(hw)?.ToString() ?? "";
            var isCpu = hwType.Equals("Cpu", StringComparison.OrdinalIgnoreCase) || underCpu;
            var isMb = hwType.Equals("Motherboard", StringComparison.OrdinalIgnoreCase)
                       || hwType.Equals("SuperIO", StringComparison.OrdinalIgnoreCase)
                       || hwType.Equals("EmbeddedController", StringComparison.OrdinalIgnoreCase);

            if (_lhmIHardware.GetProperty("Sensors")?.GetValue(hw) is System.Collections.IEnumerable sensors)
            {
                foreach (var s in sensors)
                {
                    if (s is null) continue;
                    var st = _lhmISensor!.GetProperty("SensorType")?.GetValue(s);
                    if (st is null || !Equals(st, _lhmTempEnum)) continue;
                    var name = _lhmISensor.GetProperty("Name")?.GetValue(s)?.ToString() ?? "";
                    if (name.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("TjMax", StringComparison.OrdinalIgnoreCase) && name.Contains("Distance", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var valObj = _lhmISensor.GetProperty("Value")?.GetValue(s);
                    if (valObj is null)
                    {
                        nullCount++;
                        inventory.Add($"{hwType}/{name}=null");
                        continue;
                    }

                    var val = Convert.ToSingle(valObj, CultureInfo.InvariantCulture);
                    if (val is < 10 or > 125) continue;
                    valueCount++;
                    inventory.Add($"{hwType}/{name}={val:0.0}");

                    var rank = ScoreLhmSensorName(name);
                    if (rank >= 100)
                    {
                        if (package is null || val > package) { package = val; packageName = name; }
                    }
                    else if (rank >= 95)
                    {
                        if (tctl is null || val > tctl) { tctl = val; tctlName = name; }
                    }
                    else if (rank >= 90)
                    {
                        if (ccd is null || val > ccd) { ccd = val; ccdName = name; }
                    }
                    else if (rank >= 85)
                    {
                        if (coreMax is null || val > coreMax) { coreMax = val; coreMaxName = name; }
                    }
                    else if (rank >= 50 && isCpu)
                    {
                        if (cpuNamed is null || val > cpuNamed) { cpuNamed = val; cpuNamedName = name; }
                    }
                    else if (isCpu)
                    {
                        if (anyCpu is null || val > anyCpu) { anyCpu = val; anyCpuName = name; }
                    }
                    else if (isMb || isCpu)
                    {
                        if (any is null || val > any) { any = val; anyName = name; }
                    }
                }
            }

            if (_lhmIHardware.GetProperty("SubHardware")?.GetValue(hw) is System.Collections.IEnumerable subs)
            {
                foreach (var sub in subs)
                {
                    if (sub is null) continue;
                    Consider(sub, isCpu);
                }
            }
        }

        foreach (var hw in list)
        {
            if (hw is null) continue;
            Consider(hw, underCpu: false);
        }

        if (!_lhmInventoryLogged && (inventory.Count > 0 || nullCount > 0))
        {
            _lhmInventoryLogged = true;
            var sample = string.Join("; ", inventory.Take(24));
            DiagnosticLog.WriteTemp(
                $"LHM inventory values={valueCount} nulls={nullCount} elevated={AdminLauncher.IsElevated} sample=[{sample}]");
        }

        if (package is float p)
            return new TempHit(p, "LHM:CPU Package" + (packageName is null ? "" : $" ({packageName})"));
        if (tctl is float t)
            return new TempHit(t, "LHM:Tctl" + (tctlName is null ? "" : $" ({tctlName})"));
        if (ccd is float d)
            return new TempHit(d, "LHM:CCD" + (ccdName is null ? "" : $" ({ccdName})"));
        if (coreMax is float c)
            return new TempHit(c, "LHM:CPU Core max" + (coreMaxName is null ? "" : $" ({coreMaxName})"));
        if (cpuNamed is float cn)
            return new TempHit(cn, "LHM:CPU" + (cpuNamedName is null ? "" : $" ({cpuNamedName})"));
        if (anyCpu is float ac)
            return new TempHit(ac, "LHM:CPU" + (anyCpuName is null ? "" : $" ({anyCpuName})"));
        if (any is float a)
            return new TempHit(a, "LHM:temp" + (anyName is null ? "" : $" ({anyName})"));
        return null;
    }

    /// <summary>Package &gt; Tctl &gt; CCD &gt; Core Max &gt; CPU-named &gt; other.</summary>
    private static int ScoreLhmSensorName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 10;
        if (name.Contains("Package", StringComparison.OrdinalIgnoreCase)
            || name.Contains("CPU Package", StringComparison.OrdinalIgnoreCase))
            return 100;
        if (name.Contains("Tctl", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Tdie", StringComparison.OrdinalIgnoreCase))
            return 95;
        if (name.Contains("CCD", StringComparison.OrdinalIgnoreCase))
            return 90;
        if (name.Contains("Core Max", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Max", StringComparison.OrdinalIgnoreCase))
            return 85;
        if (name.Contains("Core Average", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Average", StringComparison.OrdinalIgnoreCase))
            return 70;
        if (name.Contains("Core", StringComparison.OrdinalIgnoreCase))
            return 60;
        if (name.Contains("CPU", StringComparison.OrdinalIgnoreCase))
            return 50;
        return 20;
    }

    private static TempHit? TryReadElevatedLhmCache()
    {
        try
        {
            var path = LhmLiveCachePath;
            if (!File.Exists(path)) return null;
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            if (age > TimeSpan.FromSeconds(90)) return null;
            var json = File.ReadAllText(path);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("tempC", out var t) || t.ValueKind != System.Text.Json.JsonValueKind.Number)
                return null;
            var val = t.GetSingle();
            if (val is < 10 or > 125) return null;
            var src = root.TryGetProperty("source", out var s) ? s.GetString() : null;
            if (string.IsNullOrWhiteSpace(src)) src = "LHM:elevated-cache";
            else if (!src.StartsWith("LHM:", StringComparison.OrdinalIgnoreCase))
                src = "LHM:" + src;
            return new TempHit(val, src + " (elevated helper)");
        }
        catch
        {
            return null;
        }
    }

    private static void MaybeOfferLhmElevationOnce()
    {
        if (_lhmElevationQueued || AdminLauncher.IsElevated) return;
        // Fresh elevated cache already present — skip UAC.
        try
        {
            var path = LhmLiveCachePath;
            if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromSeconds(60))
                return;
        }
        catch { }

        _lhmElevationQueued = true;
        try
        {
            var settings = SettingsService.Load();
            var first = !settings.LhmElevationOffered;
            settings.LhmElevationOffered = true;
            SettingsService.Save(settings);
            DiagnosticLog.WriteTemp(first
                ? "LHM: offering elevated sensor probe (--lhm-sensor-probe); approve UAC for CPU Package"
                : "LHM: restarting elevated sensor probe (cache stale); approve UAC if prompted");
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    // Non-blocking soft path - UAC decline simply continues on WMI.
                    // Do NOT pass --noelevate (that blocks Verb=runas). Child probe never re-elevates.
                    AdminLauncher.TryRelaunchElevated(new[]
                    {
                        "--lhm-sensor-probe",
                        "--skip-account"
                    });
                }
                catch (Exception ex)
                {
                    DiagnosticLog.WriteTemp("LHM elevation offer failed", ex);
                }
            });
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteTemp("LHM elevation offer setup failed", ex);
        }
    }

    private static void CloseLibreHardwareMonitor()
    {
        lock (LhmGate)
        {
            try
            {
                if (_lhmComputer is not null && _lhmComputerType is not null)
                {
                    try { _lhmComputerType.GetMethod("Close")?.Invoke(_lhmComputer, null); } catch { }
                    try { (_lhmComputer as IDisposable)?.Dispose(); } catch { }
                }
            }
            finally
            {
                _lhmComputer = null;
                _lhmComputerType = null;
                _lhmIHardware = null;
                _lhmISensor = null;
                _lhmTempEnum = null;
            }
        }
    }

    /// <summary>
    /// Elevated headless probe: Open LHM, write <see cref="LhmLiveCachePath"/> for ~2 minutes, then exit.
    /// Invoked via <c>--lhm-sensor-probe</c> before UI/single-instance.
    /// </summary>
    public static int RunElevatedSensorProbe(TimeSpan? duration = null)
    {
        var until = DateTime.UtcNow + (duration ?? TimeSpan.FromHours(12));
        DiagnosticLog.WriteTemp($"LHM elevated probe start elevated={AdminLauncher.IsElevated}");
        try
        {
            while (DateTime.UtcNow < until)
            {
                var hit = TryReadLibreHardwareMonitor();
                if (hit is { } h)
                {
                    try
                    {
                        Directory.CreateDirectory(DiagnosticLog.LogDirectory);
                        var json =
                            $"{{\"tempC\":{h.Value.ToString(CultureInfo.InvariantCulture)},\"source\":{System.Text.Json.JsonSerializer.Serialize(h.Source)},\"at\":\"{DateTime.UtcNow:O}\",\"elevated\":{(AdminLauncher.IsElevated ? "true" : "false")}}}";
                        File.WriteAllText(LhmLiveCachePath, json);
                        DiagnosticLog.WriteTemp($"LHM elevated probe wrote {h.Source}={h.Value:0.0}C");
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog.WriteTemp("LHM elevated probe write failed", ex);
                    }
                }
                else
                {
                    DiagnosticLog.WriteTemp("LHM elevated probe: still no temperature values");
                }

                Thread.Sleep(5000);
            }
        }
        finally
        {
            CloseLibreHardwareMonitor();
        }

        return 0;
    }

    private static TempHit? PickBestTemp(
        List<(TempHit Hit, int Score, bool HighPrecision)> candidates,
        bool preferCpuNamed)
    {
        if (candidates.Count == 0) return null;

        IEnumerable<(TempHit Hit, int Score, bool HighPrecision)> pool = candidates;
        if (preferCpuNamed)
        {
            var cpuLike = candidates.Where(c => c.Score >= 40).ToList();
            if (cpuLike.Count > 0) pool = cpuLike;
        }

        var ranked = pool
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.HighPrecision)
            .ThenByDescending(c => c.Hit.Value)
            .ToList();

        var topScore = ranked[0].Score;
        return ranked.Where(c => c.Score == topScore).OrderByDescending(c => c.Hit.Value).First().Hit;
    }

    /// <summary>
    /// PACKAGE/PKG/CPU highest, TZ00 medium, other thermal zones low.
    /// </summary>
    private static int ScoreThermalZoneName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 10;
        if (name.Contains("PACKAGE", StringComparison.OrdinalIgnoreCase)
            || name.Contains("PKG", StringComparison.OrdinalIgnoreCase))
            return 100;
        if (name.Contains("CPU", StringComparison.OrdinalIgnoreCase)
            || name.Contains("PROC", StringComparison.OrdinalIgnoreCase))
            return 90;
        if (name.Contains("CORE", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Tctl", StringComparison.OrdinalIgnoreCase))
            return 80;
        if (name.Contains("TZ00", StringComparison.OrdinalIgnoreCase))
            return 50;
        if (name.Contains("TZ0", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Thermal Zone", StringComparison.OrdinalIgnoreCase))
            return 40;
        return 20;
    }

    private static string FormatThermalSource(string prefix, string name, int score)
    {
        if (score >= 80)
            return $"{prefix}:{name}";
        if (score >= 40)
            return $"{prefix}-{name} (thermal zone; not CPU package)";
        return $"{prefix}-{name} (thermal zone; not CPU package)";
    }

    private readonly record struct TempHit(float Value, string Source);

    private static bool IsCpuLikeZone(string name) => ScoreThermalZoneName(name) >= 40;

    /// <summary>
    /// Accepts Kelvin, tenths-of-Kelvin, or already-Celsius sensor values.
    /// HighPrecisionTemperature is tenths of Kelvin (e.g. 3482 → 75.05°C).
    /// Temperature / MSAcpi (after /10) are Kelvin (e.g. 348 → 74.85°C).
    /// </summary>
    private static float? NormalizeToCelsius(double raw)
    {
        // Tenths of Kelvin (HighPrecisionTemperature passed without pre-scale).
        if (raw is > 1000 and < 5000)
            raw /= 10.0;

        var celsius = raw >= 200 ? raw - 273.15 : raw;
        if (celsius is < 10 or > 125)
            return null;
        return (float)celsius;
    }

    private float ReadCpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return 0f;

        var idleTime = idle.ToInt64();
        var kernelTime = kernel.ToInt64();
        var userTime = user.ToInt64();

        if (!_cpuPrimed)
        {
            _idlePrev = idleTime;
            _kernelPrev = kernelTime;
            _userPrev = userTime;
            _cpuPrimed = true;
            return 0f;
        }

        var idleDelta = idleTime - _idlePrev;
        var kernelDelta = kernelTime - _kernelPrev;
        var userDelta = userTime - _userPrev;
        var total = kernelDelta + userDelta;

        _idlePrev = idleTime;
        _kernelPrev = kernelTime;
        _userPrev = userTime;

        if (total <= 0)
            return 0f;

        var busy = total - idleDelta;
        return Math.Clamp((float)(100.0 * busy / total), 0f, 100f);
    }

    private static (float Percent, double UsedGb, double TotalGb) ReadRam()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0)
            return (0f, 0, 0);

        var used = status.TotalPhys - status.AvailPhys;
        var percent = Math.Clamp((float)(100.0 * used / status.TotalPhys), 0f, 100f);
        return (percent, used / (1024.0 * 1024 * 1024), status.TotalPhys / (1024.0 * 1024 * 1024));
    }

    private static (float Percent, string Label) ReadStorage()
    {
        try
        {
            var drive = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .OrderByDescending(d => d.TotalSize)
                .FirstOrDefault();
            if (drive is null || drive.TotalSize <= 0)
                return (0f, "--");

            var used = drive.TotalSize - drive.AvailableFreeSpace;
            var percent = Math.Clamp((float)(100.0 * used / drive.TotalSize), 0f, 100f);
            var usedGb = used / (1024.0 * 1024 * 1024);
            var totalGb = drive.TotalSize / (1024.0 * 1024 * 1024);
            return (percent, $"{usedGb:0.0}/{totalGb:0.0} GB");
        }
        catch
        {
            return (0f, "--");
        }
    }

    private (bool Online, string Label, double? Mbps) ReadNetwork()
    {
        try
        {
            if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
            {
                _netBytesPrev = 0;
                _cachedMbps = null;
                return (false, "Offline", null);
            }

            long total = 0;
            string? iface = null;
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    continue;
                if (ni.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                    or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                    continue;

                var stats = ni.GetIPStatistics();
                total += stats.BytesReceived + stats.BytesSent;
                iface ??= ni.Name;
            }

            var now = DateTime.UtcNow;
            double? mbps = _cachedMbps;
            if (_netBytesPrev > 0 && _netSampleUtc != DateTime.MinValue)
            {
                var seconds = (now - _netSampleUtc).TotalSeconds;
                if (seconds > 0.2 && total >= _netBytesPrev)
                {
                    var bitsPerSec = (total - _netBytesPrev) * 8.0 / seconds;
                    mbps = bitsPerSec / 1_000_000.0;
                    _cachedMbps = mbps;
                }
            }

            _netBytesPrev = total;
            _netSampleUtc = now;

            var label = mbps is double m
                ? (m >= 1 ? $"{m:0.0} Mbps" : $"{m * 1000:0} Kbps")
                : (iface ?? "Online");
            return (true, label, mbps);
        }
        catch
        {
            return (false, "Unknown", null);
        }
    }

    private static (string? Name, float? Load) ReadGpuCached()
    {
        EnsureSlowSampler();
        return (_sGpuName, _sGpuLoad);
    }

    private static string? ProbeGpuName()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, AdapterRAM FROM Win32_VideoController");
            using var results = searcher.Get();
            string? bestName = null;
            ulong bestRam = 0;
            foreach (ManagementObject obj in results)
            {
                using (obj)
                {
                    var name = obj["Name"]?.ToString();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase))
                        continue;
                    ulong ram = 0;
                    try { ram = Convert.ToUInt64(obj["AdapterRAM"] ?? 0); } catch { }
                    if (bestName is null || ram > bestRam)
                    {
                        bestName = name;
                        bestRam = ram;
                    }
                }
            }
            return bestName;
        }
        catch
        {
            return null;
        }
    }

    private static float? ReadGpuEngineLoadCached()
    {
        try
        {
            // Localized counter sets vary; fail soft. Counters are created once and sampled
            // across ticks (no Thread.Sleep), so utilization reflects the 3s window.
            if (!_gpuCountersPrimed)
            {
                _gpuCountersPrimed = true;
                var cat = new System.Diagnostics.PerformanceCounterCategory("GPU Engine");
                var instances = cat.GetInstanceNames()
                    .Where(n => n.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                    .Take(4);
                foreach (var inst in instances)
                {
                    try
                    {
                        var counter = new System.Diagnostics.PerformanceCounter(
                            "GPU Engine", "Utilization Percentage", inst, readOnly: true);
                        _ = counter.NextValue(); // prime
                        GpuCounters[inst] = counter;
                    }
                    catch { }
                }
                return GpuCounters.Count == 0 ? null : 0f;
            }

            if (GpuCounters.Count == 0) return null;

            float sum = 0;
            foreach (var counter in GpuCounters.Values)
            {
                try { sum += counter.NextValue(); } catch { }
            }
            return Math.Clamp(sum, 0f, 100f);
        }
        catch
        {
            return null;
        }
    }

    private double? ReadChargeWattsSmoothed(bool chargerConnected)
    {
        if (!chargerConnected)
        {
            _chargeWattsEma = null;
            _lastChargeSample = null;
            return null;
        }

        var now = DateTime.UtcNow;
        if (now >= _nextChargeProbeUtc)
        {
            _nextChargeProbeUtc = now.AddSeconds(2);
            _lastChargeSample = BatteryChargeMeter.ReadChargeWatts(true);
        }

        var sample = _lastChargeSample;
        if (sample is double watts)
        {
            _chargeWattsEma = _chargeWattsEma is double prev
                ? prev * 0.55 + watts * 0.45
                : watts;
            return Math.Round(Math.Max(0, _chargeWattsEma.Value), 1);
        }

        return _chargeWattsEma is double hold ? Math.Round(Math.Max(0, hold), 1) : 0;
    }

    private static (bool Present, float? Percent, bool Charging, bool OnAc) ReadBattery()
    {
        if (!GetSystemPowerStatus(out var status))
            return (false, null, false, true);

        var onAc = status.ACLineStatus == 1;
        if (status.BatteryFlag == 128 || status.BatteryLifePercent == 255)
            return (false, null, false, onAc);

        var charging = (status.BatteryFlag & 8) != 0 || (onAc && status.BatteryLifePercent < 100);
        var percent = Math.Clamp((float)status.BatteryLifePercent, 0f, 100f);
        return (true, percent, charging, onAc);
    }

    public void Dispose()
    {
        // Instance fields only — the shared slow sampler is process-lifetime and stopped via ShutdownSampler.
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
        public long ToInt64() => ((long)HighDateTime << 32) | LowDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }
}
