using System.IO;
using System.Text;

namespace CpuTempWidget.Services;

/// <summary>Desktop / Start Menu shortcuts and uninstall helper for production installs.</summary>
public static class ShortcutService
{
    public static void EnsureInstallShortcuts(string exePath)
    {
        try
        {
            if (!File.Exists(exePath)) return;
            var startMenu = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs", "MugoByte");
            Directory.CreateDirectory(startMenu);
            CreateShortcut(Path.Combine(startMenu, "Pulse.lnk"), exePath, "Pulse by MugoByte Technologies");

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            CreateShortcut(Path.Combine(desktop, "Pulse.lnk"), exePath, "Pulse by MugoByte Technologies");
            EnsureStartupShortcut(exePath, enabled: true);

            WriteUninstaller(exePath);
        }
        catch { }
    }

    public static void EnsureStartupShortcut(string exePath, bool enabled)
    {
        try
        {
            var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            Directory.CreateDirectory(startup);
            var lnk = Path.Combine(startup, "Pulse.lnk");
            if (!enabled)
            {
                if (File.Exists(lnk)) File.Delete(lnk);
                return;
            }
            if (!File.Exists(exePath)) return;
            CreateShortcut(lnk, exePath, "Pulse by MugoByte Technologies");
        }
        catch { }
    }

    public static void WriteUninstaller(string exePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrWhiteSpace(dir)) return;
            var script = Path.Combine(dir, "Uninstall-Pulse.ps1");
            var content = """
                #Requires -Version 5.1
                $ErrorActionPreference = 'SilentlyContinue'
                Get-Process Pulse -ErrorAction SilentlyContinue | Stop-Process -Force
                Start-Sleep -Seconds 1
                Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Pulse' -ErrorAction SilentlyContinue
                schtasks /Delete /F /TN "MugoByte Pulse" | Out-Null
                $start = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\MugoByte'
                Remove-Item (Join-Path $start 'Pulse.lnk') -Force -ErrorAction SilentlyContinue
                Remove-Item (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Pulse.lnk') -Force -ErrorAction SilentlyContinue
                Remove-Item (Join-Path ([Environment]::GetFolderPath('Startup')) 'Pulse.lnk') -Force -ErrorAction SilentlyContinue
                $install = Join-Path $env:LOCALAPPDATA 'MugoByte\Pulse'
                if (Test-Path $install) { Remove-Item $install -Recurse -Force }
                Write-Host 'Pulse uninstalled (user install). Settings under %AppData%\MugoByte\Pulse were left in place.'
                """;
            File.WriteAllText(script, content, Encoding.UTF8);

            CreateShortcut(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                    "Programs", "MugoByte", "Uninstall Pulse.lnk"),
                "powershell.exe",
                "Uninstall Pulse",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"");
        }
        catch { }
    }

    private static void CreateShortcut(string lnkPath, string target, string description, string? args = null)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return;
        dynamic shell = Activator.CreateInstance(shellType)!;
        var shortcut = shell.CreateShortcut(lnkPath);
        shortcut.TargetPath = target;
        if (!string.IsNullOrWhiteSpace(args))
            shortcut.Arguments = args;
        shortcut.WorkingDirectory = Path.GetDirectoryName(target) ?? "";
        shortcut.Description = description;
        shortcut.Save();
    }
}
