using Microsoft.Win32;

namespace CpuTempWidget.Services;

/// <summary>Lists and toggles HKCU Run startup entries (user-level).</summary>
public static class StartupAppsService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public sealed record Entry(string Name, string Command, bool Enabled);

    public static IReadOnlyList<Entry> ListUserRun()
    {
        var list = new List<Entry>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            if (key is null) return list;
            foreach (var name in key.GetValueNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                var val = key.GetValue(name)?.ToString() ?? "";
                list.Add(new Entry(name, val, Enabled: true));
            }
        }
        catch { }

        // Disabled entries live under StartupApproved\Run (binary) — surface names only when present.
        try
        {
            using var approved = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", false);
            if (approved is null) return list;
            foreach (var name in approved.GetValueNames())
            {
                var raw = approved.GetValue(name) as byte[];
                if (raw is null || raw.Length == 0) continue;
                // First byte 0x03 typically means disabled in modern Windows.
                var disabled = raw[0] is 0x03 or 0x02;
                var existing = list.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
                if (existing is not null && disabled)
                {
                    list.Remove(existing);
                    list.Add(existing with { Enabled = false });
                }
            }
        }
        catch { }

        return list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool TryRemove(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
            return true;
        }
        catch { return false; }
    }
}
