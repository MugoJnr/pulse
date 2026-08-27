using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace CpuTempWidget.Services;

/// <summary>
/// Launch tools and admin commands with no confirmation dialogs.
/// App self-elevates at startup so these run with full rights when possible.
/// </summary>
public static class AdminLauncher
{
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Relaunch this process elevated (UAC once). Returns true if a new elevated process was started.
    /// No MessageBox — decline UAC simply continues non-elevated.
    /// </summary>
    public static bool TryRelaunchElevated(string[] args)
    {
        if (IsElevated) return false;
        if (args.Any(a => a.Equals("--noelevate", StringComparison.OrdinalIgnoreCase)))
            return false;

        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return false;

            var argLine = string.Join(" ",
                args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = argLine,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            // User cancelled UAC or elevation unavailable.
            return false;
        }
    }

    public static void Shell(string file, string? args = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file, args ?? string.Empty)
            {
                UseShellExecute = true
            });
        }
        catch { }
    }

    public static void Uri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch { }
    }

    public static void Cmd(string command, bool wait = false)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c " + command)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var p = Process.Start(psi);
            if (wait)
                p?.WaitForExit(120_000);
        }
        catch { }
    }

    public static void PowerShell(string command, bool wait = false)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command " + Quote(command))
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var p = Process.Start(psi);
            if (wait)
                p?.WaitForExit(180_000);
        }
        catch { }
    }

    public static void CmdVisible(string command) =>
        Shell("cmd.exe", "/k " + command);

    public static void PowerShellVisible(string command) =>
        Shell("powershell.exe", "-NoExit -ExecutionPolicy Bypass -Command " + Quote(command));

    private static string Quote(string s) =>
        "\"" + s.Replace("\"", "\\\"") + "\"";
}
