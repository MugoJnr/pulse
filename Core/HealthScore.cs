using System.IO;
using CpuTempWidget.Services;

namespace CpuTempWidget.Core;

public static class HealthScore
{
    public sealed record Result(int Score, string Label, string Detail);

    public static Result Compute(SystemReading r)
    {
        double total = 0;
        double weight = 0;

        void Add(double w, double points)
        {
            total += w * points;
            weight += w;
        }

        // CPU: lower utilization better when idle-ish; >90 harsh
        Add(1.2, r.CpuPercent switch
        {
            < 40 => 100,
            < 70 => 80,
            < 90 => 55,
            _ => 30
        });

        Add(1.2, r.RamPercent switch
        {
            < 60 => 100,
            < 80 => 75,
            < 92 => 50,
            _ => 25
        });

        if (r.TemperatureC is float t)
        {
            Add(1.4, t switch
            {
                < 60 => 100,
                < 75 => 80,
                < 85 => 55,
                _ => 25
            });
        }

        Add(1.0, r.StoragePercent switch
        {
            < 70 => 100,
            < 85 => 75,
            < 95 => 45,
            _ => 20
        });

        if (r.BatteryPresent && r.BatteryPercent is float b)
        {
            Add(0.8, b switch
            {
                > 40 => 100,
                > 20 => 70,
                > 10 => 45,
                _ => 20
            });
        }

        Add(0.5, r.NetworkOnline ? 100 : 40);

        if (r.GpuLoadPercent is float gpu)
        {
            Add(0.7, gpu switch
            {
                < 50 => 100,
                < 80 => 75,
                < 95 => 50,
                _ => 30
            });
        }

        var score = weight <= 0 ? 50 : (int)Math.Round(Math.Clamp(total / weight, 0, 100));
        var label = score switch
        {
            >= 90 => "Excellent",
            >= 75 => "Good",
            >= 55 => "Fair",
            _ => "Needs attention"
        };
        var detail =
            $"CPU {r.CpuPercent:0}% · RAM {r.RamPercent:0}% · Disk {r.StoragePercent:0}%" +
            (r.TemperatureC is float tt ? $" · {tt:0}°C" : "") +
            (r.GpuLoadPercent is float gg ? $" · GPU {gg:0}%" : "") +
            $" · {r.NetworkLabel}";
        return new Result(score, label, detail);
    }
}

public static class DiagnosticsInfo
{
    public static IEnumerable<(string Label, string Value)> Lines(SystemReading? reading = null)
    {
        yield return ("Pulse", Branding.Version);
        yield return ("Company", Branding.Company);
        yield return ("Machine", Environment.MachineName);
        yield return ("User", Environment.UserName);
        yield return ("OS", Environment.OSVersion.ToString());
        yield return ("64-bit OS", Environment.Is64BitOperatingSystem.ToString());
        yield return ("Logical processors", Environment.ProcessorCount.ToString());
        yield return (".NET", Environment.Version.ToString());
        yield return ("Elevated", AdminLauncher.IsElevated ? "Yes" : "No");
        yield return ("Install", BootstrapService.InstalledExecutable);

        if (reading is not null)
        {
            yield return ("CPU load", $"{reading.CpuPercent:0}%");
            yield return ("RAM", $"{reading.RamUsedGb:0.0} / {reading.RamTotalGb:0.0} GB");
            yield return ("Temp", reading.TemperatureC is float t ? $"{t:0}°C" : "n/a");
            yield return ("GPU", reading.GpuName ?? "n/a");
            yield return ("GPU load", reading.GpuLoadPercent is float g ? $"{g:0}%" : "n/a");
            yield return ("Storage", reading.StorageLabel);
            yield return ("Network", reading.NetworkLabel);
            yield return ("Battery", reading.BatteryPresent && reading.BatteryPercent is float b
                ? $"{b:0}%" : (reading.OnAcPower ? "AC" : "n/a"));
            yield return ("Alerts open", NotificationCenter.UnresolvedCount.ToString());
        }

        foreach (var d in DriveInfo.GetDrives().Where(x => x.IsReady).Take(6))
        {
            var used = d.TotalSize - d.AvailableFreeSpace;
            yield return ($"Drive {d.Name}", $"{used / 1e9:0.0}/{d.TotalSize / 1e9:0.0} GB");
        }
    }
}
