using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CpuTempWidget.Models;
using Microsoft.Win32;

namespace CpuTempWidget.Services;

public static class SettingsService
{
    private const string LegacyFolderName = "CpuTempWidget";
    private const string LegacyFolderName2 = "SystemMonitor";
    private const string AppFolderName = "Pulse";
    private const string FileName = "settings.json";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Pulse";
    private const string LegacyRunValueName = "CpuTempWidget";
    private const string LegacyRunValueName2 = "MugoByteSystemMonitor";
    private const string TaskName = "MugoByte Pulse";
    private const string LegacyTaskName = "CpuTempWidget";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static AppSettings? _cached;

    private static string SettingsDirectory
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MugoByte",
                AppFolderName);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string SettingsPath => Path.Combine(SettingsDirectory, FileName);

    public static AppSettings Load()
    {
        if (_cached is not null) return _cached;
        try
        {
            MigrateLegacySettingsIfNeeded();
            if (!File.Exists(SettingsPath))
                return _cached = new AppSettings();

            var json = File.ReadAllText(SettingsPath);
            return _cached = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return _cached = new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        _cached = settings;
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, json);
    }

    private static void MigrateLegacySettingsIfNeeded()
    {
        if (File.Exists(SettingsPath)) return;

        foreach (var legacy in LegacyPaths())
        {
            try
            {
                if (!File.Exists(legacy)) continue;
                Directory.CreateDirectory(SettingsDirectory);
                File.Copy(legacy, SettingsPath, overwrite: false);
                return;
            }
            catch { }
        }
    }

    private static IEnumerable<string> LegacyPaths()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(appData, LegacyFolderName, FileName);
        yield return Path.Combine(appData, "MugoByte", LegacyFolderName2, FileName);
    }

    public static string ResolveLaunchExecutable(string? preferred = null)
    {
        if (!string.IsNullOrWhiteSpace(preferred) && File.Exists(preferred)
            && !Path.GetFileName(preferred).Contains("Setup", StringComparison.OrdinalIgnoreCase))
            return preferred;

        var installed = BootstrapService.InstalledExecutable;
        if (File.Exists(installed))
            return installed;

        return preferred ?? Environment.ProcessPath ?? "";
    }

    public static void ApplyStartup(bool enabled, string executablePath)
    {
        TryDisableLegacyTask();
        var exe = ResolveLaunchExecutable(executablePath);
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            return;

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, true);

        foreach (var legacy in new[] { LegacyRunValueName, LegacyRunValueName2 })
        {
            if (key.GetValue(legacy) is not null)
                key.DeleteValue(legacy, throwOnMissingValue: false);
        }

        if (enabled)
        {
            key.SetValue(RunValueName, $"\"{exe}\"");
            EnsureLogonTask(exe);
            ShortcutService.EnsureStartupShortcut(exe, enabled: true);
        }
        else
        {
            if (key.GetValue(RunValueName) is not null)
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            DeleteLogonTask();
            ShortcutService.EnsureStartupShortcut(exe, enabled: false);
        }
    }

    public static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        if (key?.GetValue(RunValueName) is string)
            return true;
        if (key?.GetValue(LegacyRunValueName) is string)
            return true;
        if (key?.GetValue(LegacyRunValueName2) is string)
            return true;
        return LogonTaskExists();
    }

    private static void EnsureLogonTask(string exe)
    {
        try
        {
            RunSilent("schtasks.exe",
                $"/Create /F /TN \"{TaskName}\" /SC ONLOGON /DELAY 0000:15 /RL LIMITED /TR \"\\\"{exe}\\\"\"");
        }
        catch { }
    }

    private static void DeleteLogonTask()
    {
        try { RunSilent("schtasks.exe", $"/Delete /F /TN \"{TaskName}\""); }
        catch { }
    }

    private static bool LogonTaskExists()
    {
        try
        {
            var code = RunSilent("schtasks.exe", $"/Query /TN \"{TaskName}\"");
            return code == 0;
        }
        catch
        {
            return false;
        }
    }

    private static int RunSilent(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(psi);
        if (process is null) return -1;
        process.WaitForExit(8000);
        return process.HasExited ? process.ExitCode : -1;
    }

    private static void TryDisableLegacyTask()
    {
        try
        {
            RunSilent("schtasks.exe", $"/Change /TN \"{LegacyTaskName}\" /DISABLE");
            RunSilent("schtasks.exe", $"/Delete /F /TN \"{LegacyTaskName}\"");
        }
        catch { }
    }
}
