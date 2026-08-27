using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace CpuTempWidget.Services;

public static class QuickActionsService
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static void SetDarkMode() => SetAppsTheme(0);
    public static void SetLightMode() => SetAppsTheme(1);

    private static void SetAppsTheme(int light)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(PersonalizeKey, true);
            key.SetValue("AppsUseLightTheme", light, RegistryValueKind.DWord);
            key.SetValue("SystemUsesLightTheme", light, RegistryValueKind.DWord);
        }
        catch { }
    }

    public static void RestartExplorer()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
            }
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }
        catch { }
    }

    public static void FlushDns() => AdminLauncher.Cmd("ipconfig /flushdns", wait: true);

    public static void ReleaseRenewIp() =>
        AdminLauncher.Cmd("ipconfig /release & ipconfig /renew", wait: true);

    public static void ResetWinsock() =>
        AdminLauncher.Cmd("netsh winsock reset & netsh int ip reset", wait: true);

    public static void ClearTemp()
    {
        try
        {
            foreach (var temp in new[]
                     {
                         Path.GetTempPath(),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")
                     })
            {
                if (!Directory.Exists(temp)) continue;
                foreach (var file in Directory.EnumerateFiles(temp))
                {
                    try { File.Delete(file); } catch { }
                }
                foreach (var dir in Directory.EnumerateDirectories(temp))
                {
                    try { Directory.Delete(dir, recursive: true); } catch { }
                }
            }
        }
        catch { }
    }

    public static void ClearPrefetch() =>
        AdminLauncher.Cmd(@"del /q /f /s %SystemRoot%\Prefetch\*.* >nul 2>&1", wait: true);

    public static void ClearThumbnailCache() =>
        AdminLauncher.Cmd(
            @"taskkill /f /im explorer.exe & del /f /s /q %LocalAppData%\Microsoft\Windows\Explorer\thumbcache_*.db & start explorer.exe",
            wait: true);

    public static void EmptyRecycleBin() =>
        AdminLauncher.PowerShell(
            "Clear-RecycleBin -Force -ErrorAction SilentlyContinue",
            wait: true);

    public static void ClearClipboard() =>
        AdminLauncher.PowerShell("Set-Clipboard -Value $null", wait: true);

    public static void RunSfc() =>
        AdminLauncher.CmdVisible("sfc /scannow");

    public static void RunDism() =>
        AdminLauncher.CmdVisible("DISM /Online /Cleanup-Image /RestoreHealth");

    public static void OptimizeDrives() =>
        AdminLauncher.Shell("dfrgui");

    public static void CheckDisk() =>
        AdminLauncher.CmdVisible("chkdsk C: /scan");

    public static void CreateRestorePoint() =>
        AdminLauncher.PowerShell(
            "Checkpoint-Computer -Description 'Pulse restore point' -RestorePointType MODIFY_SETTINGS",
            wait: true);

    public static void BatteryReport()
    {
        var outPath = Path.Combine(Path.GetTempPath(), "pulse-battery-report.html");
        AdminLauncher.Cmd($"powercfg /batteryreport /output \"{outPath}\" & start \"\" \"{outPath}\"", wait: true);
    }

    public static void EnableUltimatePerformance()
    {
        AdminLauncher.Cmd("powercfg -duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61", wait: true);
        SetPowerPlan("Ultimate Performance");
    }

    // Well-known scheme GUIDs — locale-proof (names vary on non-English Windows).
    private static readonly Dictionary<string, string> KnownPlanAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["high performance"] = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
        ["balanced"] = "381b4222-f694-41f0-9685-ff5bb260df2e",
        ["power saver"] = "a1841308-3541-4fbb-9ca2-3f46316508ad"
    };

    public static void SetPowerPlan(string nameContains)
    {
        try
        {
            var alias = KnownPlanAliases.TryGetValue(nameContains, out var g) ? g : null;
            var list = RunCapture("powercfg", "/L");
            string? fallback = null;
            foreach (var line in list.Split('\n'))
            {
                var guid = ExtractGuid(line);
                if (guid is null) continue;

                if (alias is not null && guid.Equals(alias, StringComparison.OrdinalIgnoreCase))
                {
                    ActivateScheme(guid);
                    return;
                }
                if (fallback is null && line.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                    fallback = guid;
            }
            if (fallback is not null)
                ActivateScheme(fallback);
        }
        catch { }
    }

    private static void ActivateScheme(string guid)
    {
        try
        {
            Process.Start(new ProcessStartInfo("powercfg", $"/S {guid}")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            })?.WaitForExit(4000);
        }
        catch { }
    }

    public static void OpenTaskManager() => AdminLauncher.Shell("taskmgr");
    public static void OpenDeviceManager() => AdminLauncher.Shell("devmgmt.msc");
    public static void OpenWindowsSecurity() => AdminLauncher.Uri("windowsdefender:");
    public static void OpenWifiSettings() => AdminLauncher.Uri("ms-settings:network-wifi");
    public static void OpenBluetoothSettings() => AdminLauncher.Uri("ms-settings:bluetooth");
    public static void OpenStorageSense() => AdminLauncher.Uri("ms-settings:storagepolicies");
    public static void OpenDiskCleanup() => AdminLauncher.Shell("cleanmgr");
    public static void OpenHostsFile() =>
        AdminLauncher.Shell("notepad", @"C:\Windows\System32\drivers\etc\hosts");

    public static void KillProcessTree(int pid)
    {
        try
        {
            Process.GetProcessById(pid).Kill(entireProcessTree: true);
        }
        catch { }
    }

    private static string RunCapture(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        if (p is null) return string.Empty;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(5000);
        return output;
    }

    private static string? ExtractGuid(string line)
    {
        var start = line.IndexOf('{');
        var end = line.IndexOf('}');
        if (start < 0 || end <= start) return null;
        return line[start..(end + 1)];
    }
}
