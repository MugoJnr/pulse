using System.IO;

namespace CpuTempWidget.Services;

public static class NotificationCenter
{
    public sealed record Note(DateTime Utc, string Title, string Detail, bool Resolved);

    private static readonly List<Note> _notes = [];
    private static readonly Dictionary<string, DateTime> _lastPush = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();
    private static bool _loaded;

    private static string StorePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MugoByte", "Pulse", "notifications.json");

    public static IReadOnlyList<Note> All
    {
        get
        {
            EnsureLoaded();
            lock (Gate) return _notes.ToList();
        }
    }

    public static int UnresolvedCount
    {
        get
        {
            EnsureLoaded();
            lock (Gate) return _notes.Count(n => !n.Resolved);
        }
    }

    public static void Push(string title, string detail, bool resolved = false, TimeSpan? cooldown = null)
    {
        EnsureLoaded();
        var wait = cooldown ?? TimeSpan.FromMinutes(10);
        lock (Gate)
        {
            if (_lastPush.TryGetValue(title, out var last) && DateTime.UtcNow - last < wait)
                return;
            _lastPush[title] = DateTime.UtcNow;
            _notes.Insert(0, new Note(DateTime.UtcNow, title, detail, resolved));
            if (_notes.Count > 100) _notes.RemoveRange(100, _notes.Count - 100);
            PersistUnlocked();
        }
    }

    public static void MarkResolved(string title)
    {
        EnsureLoaded();
        lock (Gate)
        {
            var changed = false;
            for (var i = 0; i < _notes.Count; i++)
            {
                if (!string.Equals(_notes[i].Title, title, StringComparison.OrdinalIgnoreCase)) continue;
                if (_notes[i].Resolved) continue;
                _notes[i] = _notes[i] with { Resolved = true };
                changed = true;
            }
            if (changed)
                PersistUnlocked();
        }
    }

    public static void Evaluate(SystemReading r)
    {
        if (r.TemperatureC is float t && t >= 84)
            Push("CPU temperature high", $"{t:0}°C");
        else
            MarkResolved("CPU temperature high");

        if (r.RamPercent >= 92)
            Push("Memory almost full", $"{r.RamPercent:0}%");
        else if (r.RamPercent < 85)
            MarkResolved("Memory almost full");

        if (r.StoragePercent >= 92)
            Push("Storage running low", $"{r.StoragePercent:0}% used");
        else if (r.StoragePercent < 85)
            MarkResolved("Storage running low");

        if (r.BatteryPresent && r.BatteryPercent is float b && b <= 15 && !r.IsCharging)
            Push("Battery low", $"{b:0}%");
        else
            MarkResolved("Battery low");

        if (!r.NetworkOnline)
            Push("Internet lost", "Network offline");
        else
            MarkResolved("Internet lost");

        if (r.GpuLoadPercent is float g && g >= 95)
            Push("GPU under heavy load", $"{g:0}%");
        else if (r.GpuLoadPercent is float g2 && g2 < 80)
            MarkResolved("GPU under heavy load");
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (Gate)
        {
            if (_loaded) return;
            try
            {
                if (File.Exists(StorePath))
                {
                    var json = File.ReadAllText(StorePath);
                    var list = System.Text.Json.JsonSerializer.Deserialize<List<Note>>(json);
                    if (list is not null) _notes.AddRange(list.Take(100));
                }
            }
            catch { }
            _loaded = true;
        }
    }

    private static void PersistUnlocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, System.Text.Json.JsonSerializer.Serialize(_notes));
        }
        catch { }
    }
}
