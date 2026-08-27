using System.Diagnostics;
using System.IO;
using System.Windows;

namespace CpuTempWidget.Services;

public static class SystemPowerService
{
    public static void RestartPulse()
    {
        var exe = SettingsService.ResolveLaunchExecutable(Environment.ProcessPath);
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            return;

        // Delay so this process can drop the single-instance mutex before the child starts.
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c timeout /t 2 /nobreak >nul & start \"\" \"{exe}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });
        Application.Current.Shutdown();
    }

    public static void ShutdownComputer()
    {
        Process.Start(new ProcessStartInfo("shutdown", "/s /t 0")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }

    public static void RestartComputer()
    {
        Process.Start(new ProcessStartInfo("shutdown", "/r /t 0")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }
}
