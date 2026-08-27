using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace CpuTempWidget.Core;

/// <summary>Process end/suspend/priority with protected-process safeguards.</summary>
public static class ProcessProtection
{
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss", "wininit", "winlogon", "services", "lsass", "smss", "System",
        "Registry", "Idle", "Memory Compression", "Secure System",
        "dwm", "fontdrvhost", "sihost", "taskhostw", "RuntimeBroker",
        "SearchIndexer", "SearchHost", "StartMenuExperienceHost",
        "ShellExperienceHost", "TextInputHost", "ctfmon",
        "MsMpEng", "NisSrv", "SecurityHealthService",
        "svchost", "conhost", "dllhost", "WmiPrvSE",
        "Pulse", "Pulse-Setup"
    };

    public static bool IsProtected(string processName) =>
        Protected.Contains(processName);

    public static bool ConfirmTerminate(string processName, int pid, bool tree)
    {
        if (!IsProtected(processName))
            return SafetyService.Confirm(new PulseCommand(
                $"proc.end.{pid}",
                tree ? $"Kill tree {processName}" : $"End {processName}",
                $"PID {pid}",
                "\uE7F4",
                "applications",
                () => { },
                SearchResultKind.Process,
                isDestructive: true));

        var result = System.Windows.MessageBox.Show(
            $"“{processName}” (PID {pid}) is a protected Windows process.\n\n" +
            "Ending it can crash the desktop or lock the session.\n\n" +
            "Only continue if you fully understand the risk.",
            Branding.ProductName,
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Stop);
        return result == System.Windows.MessageBoxResult.OK;
    }

    public static bool TryEnd(int pid, bool entireTree)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            var name = p.ProcessName;
            if (!ConfirmTerminate(name, pid, entireTree))
            {
                ActivityStore.AppendLog($"proc.{pid}", name, "cancelled-protected-or-safety");
                return false;
            }

            p.Kill(entireProcessTree: entireTree);
            ActivityStore.AppendLog($"proc.{pid}", name, entireTree ? "killed-tree" : "ended");
            return true;
        }
        catch (Exception ex)
        {
            ActivityStore.AppendLog($"proc.{pid}", "end", "error:" + ex.Message);
            System.Windows.MessageBox.Show(
                "Could not end process:\n" + ex.Message,
                Branding.ProductName,
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return false;
        }
    }

    public static bool TrySuspend(int pid)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            if (IsProtected(p.ProcessName) &&
                !ConfirmTerminate(p.ProcessName, pid, tree: false))
                return false;

            foreach (ProcessThread t in p.Threads)
            {
                var handle = OpenThread(ThreadAccess.SUSPEND_RESUME, false, (uint)t.Id);
                if (handle == IntPtr.Zero) continue;
                SuspendThread(handle);
                CloseHandle(handle);
            }
            ActivityStore.AppendLog($"proc.{pid}", p.ProcessName, "suspended");
            return true;
        }
        catch (Exception ex)
        {
            ActivityStore.AppendLog($"proc.{pid}", "suspend", "error:" + ex.Message);
            return false;
        }
    }

    public static bool TryResume(int pid)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            foreach (ProcessThread t in p.Threads)
            {
                var handle = OpenThread(ThreadAccess.SUSPEND_RESUME, false, (uint)t.Id);
                if (handle == IntPtr.Zero) continue;
                while (ResumeThread(handle) > 0) { }
                CloseHandle(handle);
            }
            ActivityStore.AppendLog($"proc.{pid}", p.ProcessName, "resumed");
            return true;
        }
        catch (Exception ex)
        {
            ActivityStore.AppendLog($"proc.{pid}", "resume", "error:" + ex.Message);
            return false;
        }
    }

    public static bool TrySetPriority(int pid, ProcessPriorityClass priority)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            if (IsProtected(p.ProcessName))
            {
                var go = System.Windows.MessageBox.Show(
                    $"Change priority of protected process “{p.ProcessName}”?",
                    Branding.ProductName,
                    System.Windows.MessageBoxButton.OKCancel,
                    System.Windows.MessageBoxImage.Warning);
                if (go != System.Windows.MessageBoxResult.OK) return false;
            }

            p.PriorityClass = priority;
            ActivityStore.AppendLog($"proc.{pid}", p.ProcessName, "priority:" + priority);
            return true;
        }
        catch (Exception ex)
        {
            ActivityStore.AppendLog($"proc.{pid}", "priority", "error:" + ex.Message);
            return false;
        }
    }

    public static bool TryOpenLocation(int pid)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            var path = p.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true
            });
            ActivityStore.AppendLog($"proc.{pid}", p.ProcessName, "open-location");
            return true;
        }
        catch (Exception ex)
        {
            ActivityStore.AppendLog($"proc.{pid}", "open-location", "error:" + ex.Message);
            return false;
        }
    }

    [Flags]
    private enum ThreadAccess : int
    {
        SUSPEND_RESUME = 0x0002
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenThread(ThreadAccess dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

    [DllImport("kernel32.dll")]
    private static extern uint SuspendThread(IntPtr hThread);

    [DllImport("kernel32.dll")]
    private static extern int ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
