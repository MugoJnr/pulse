using System.Diagnostics;
using System.IO;
using CpuTempWidget.Services;

namespace CpuTempWidget.Core;

public sealed class SearchHit
{
    public required IPulseCommand Command { get; init; }
    public int Score { get; init; }
    public string? Badge { get; init; }
}

public static class SearchEngine
{
    private static List<IPulseCommand>? _appCommands;
    private static DateTime _appsUtc = DateTime.MinValue;
    private static List<IPulseCommand>? _storeApps;
    private static DateTime _storeUtc = DateTime.MinValue;

    public static IReadOnlyList<SearchHit> Search(string query, int limit = 32)
    {
        var q = (query ?? string.Empty).Trim();
        var pool = new List<IPulseCommand>();
        pool.AddRange(ModuleRegistry.AllCommands());
        pool.AddRange(GetAppCommands());
        pool.AddRange(GetStoreAppCommandsCached());
        pool.AddRange(GetProcessCommands(q));
        pool.AddRange(GetFileCommands(q));

        // Category navigators
        foreach (var m in ModuleRegistry.All)
        {
            pool.Add(new PulseCommand(
                $"nav.{m.Id}", m.Label, "Open module", m.Glyph, m.Id,
                () => PulseHost.ShowCategory(m.Id),
                SearchResultKind.Category, keywords: ["module", m.Id]));
        }

        // Favorites / recent as searchable
        foreach (var fav in ActivityStore.GetFavorites())
        {
            var cmd = pool.FirstOrDefault(c => c.Id == fav.CommandId);
            if (cmd is not null)
                pool.Add(new PulseCommand(
                    "fav." + cmd.Id, cmd.Title, "Favorite · " + cmd.Subtitle, cmd.Glyph, cmd.ModuleId,
                    () => CommandDispatcher.Execute(cmd),
                    SearchResultKind.Favorite, cmd.IsDestructive, cmd.RequiresElevation,
                    cmd.Keywords.ToArray()));
        }

        if (string.IsNullOrWhiteSpace(q))
        {
            // Only surface recents whose command still exists in the pool —
            // never hand back no-op placeholders.
            return ActivityStore.GetRecent(12)
                .Select(r => pool.FirstOrDefault(c => c.Id == r.CommandId))
                .OfType<IPulseCommand>()
                .Select(cmd => new SearchHit { Command = cmd, Score = 50, Badge = "Recent" })
                .Take(limit)
                .ToList();
        }

        return pool
            .DistinctBy(c => c.Id + c.Kind)
            .Select(c => new SearchHit { Command = c, Score = SmartRanking.Score(c, q), Badge = BadgeFor(c) })
            .Where(h => h.Score > 0)
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Command.Title)
            .Take(limit)
            .ToList();
    }

    private static string? BadgeFor(IPulseCommand c) =>
        c.Kind switch
        {
            SearchResultKind.Favorite => "Favorite",
            SearchResultKind.Recent => "Recent",
            SearchResultKind.Process => "Process",
            SearchResultKind.Application => "App",
            SearchResultKind.Setting => "Settings",
            SearchResultKind.File => "File",
            SearchResultKind.Folder => "Folder",
            SearchResultKind.Category => "Module",
            _ => null
        };

    private static IEnumerable<IPulseCommand> GetAppCommands()
    {
        if (_appCommands is not null && DateTime.UtcNow - _appsUtc < TimeSpan.FromMinutes(10))
            return _appCommands;

        var list = new List<IPulseCommand>();
        try
        {
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
                     })
            {
                if (!Directory.Exists(root)) continue;
                foreach (var link in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).Take(400))
                {
                    var name = Path.GetFileNameWithoutExtension(link);
                    if (string.IsNullOrWhiteSpace(name) || name.StartsWith("uninstall", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var path = link;
                    list.Add(new PulseCommand(
                        "app." + name.ToLowerInvariant(),
                        name, "Application", "\uE71D", "applications",
                        () => AdminLauncher.Shell("explorer", path),
                        SearchResultKind.Application, keywords: [name]));
                }
            }
        }
        catch { }

        _appCommands = list;
        _appsUtc = DateTime.UtcNow;
        return list;
    }

    /// <summary>Store / system apps from shell:AppsFolder (no .lnk on disk on Win11).</summary>
    private static IEnumerable<IPulseCommand> GetStoreAppCommands()
    {
        var list = new List<IPulseCommand>();
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return list;
            dynamic shell = Activator.CreateInstance(shellType)!;
            var folder = shell.NameSpace("shell:AppsFolder");
            foreach (var item in folder.Items())
            {
                try
                {
                    var name = (string)item.Name;
                    var aumid = (string)item.Path;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(aumid)) continue;
                    if (!aumid.Contains('!')) continue; // desktop apps already covered by .lnk scan
                    if (name.StartsWith("uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                    var local = aumid;
                    list.Add(new PulseCommand(
                        "storeapp." + local.ToLowerInvariant(),
                        name, "App", "\uE71D", "applications",
                        () => AdminLauncher.Shell("explorer", "shell:appsFolder\\" + local),
                        SearchResultKind.Application, keywords: [name]));
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    private static IEnumerable<IPulseCommand> GetStoreAppCommandsCached()
    {
        if (_storeApps is not null && DateTime.UtcNow - _storeUtc < TimeSpan.FromMinutes(10))
            return _storeApps;
        _storeApps = GetStoreAppCommands().ToList();
        _storeUtc = DateTime.UtcNow;
        return _storeApps;
    }

    private static IEnumerable<IPulseCommand> GetProcessCommands(string query)
    {
        if (query.Length < 2) yield break;
        Process[] procs;
        try { procs = Process.GetProcesses(); }
        catch { yield break; }

        try
        {
            foreach (var p in procs.Take(80))
            {
                string name;
                int pid;
                try { name = p.ProcessName; pid = p.Id; }
                catch { continue; }

                if (!name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                var localPid = pid;
                var localName = name;
                yield return new PulseCommand(
                    $"proc.end.{localPid}",
                    $"End {localName}",
                    $"PID {localPid}",
                    "\uE7F4", "applications",
                    () => ProcessProtection.TryEnd(localPid, entireTree: false),
                    SearchResultKind.Process,
                    isDestructive: true,
                    keywords: [localName, "kill", "end"]);
            }
        }
        finally
        {
            foreach (var p in procs)
            {
                try { p.Dispose(); } catch { }
            }
        }
    }

    private static IEnumerable<IPulseCommand> GetFileCommands(string query)
    {
        if (query.Length < 2) yield break;
        // NOTE: history is recorded at execute-time (PulseMainWindow), never per keystroke.

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };

        var found = 0;
        const int maxResults = 48;
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var path in EnumerateShallow(root, maxDepth: 3, maxNodes: 400))
            {
                if (found >= maxResults) yield break;
                var name = Path.GetFileName(path);
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

                var isDir = Directory.Exists(path);
                var local = path;
                found++;
                yield return new PulseCommand(
                    (isDir ? "folder." : "file.") + local.ToLowerInvariant(),
                    name,
                    local,
                    isDir ? "\uE8B7" : "\uE8A5",
                    "developer",
                    () => AdminLauncher.Shell("explorer", isDir ? local : $"/select,\"{local}\""),
                    isDir ? SearchResultKind.Folder : SearchResultKind.File,
                    keywords: [name, Path.GetExtension(name).Trim('.')]);
            }
        }
    }

    private static IEnumerable<string> EnumerateShallow(string root, int maxDepth, int maxNodes)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        var seen = 0;
        while (queue.Count > 0 && seen < maxNodes)
        {
            var (path, depth) = queue.Dequeue();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(path); }
            catch { continue; }

            foreach (var entry in entries)
            {
                seen++;
                if (seen > maxNodes) yield break;
                yield return entry;
                if (depth >= maxDepth) continue;
                try
                {
                    if (Directory.Exists(entry))
                        queue.Enqueue((entry, depth + 1));
                }
                catch { }
            }
        }
    }
}

/// <summary>Persisted recent search queries for ranking / empty-state hints.</summary>
public static class SearchHistory
{
    private static readonly object Gate = new();
    private static List<string>? _items;
    private static string PathFile =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MugoByte", "Pulse", "search-history.json");

    public static IReadOnlyList<string> Recent(int limit = 12)
    {
        Ensure();
        lock (Gate) return _items!.Take(limit).ToList();
    }

    public static void Remember(string query)
    {
        query = query.Trim();
        if (query.Length < 2) return;
        Ensure();
        lock (Gate)
        {
            _items!.RemoveAll(x => string.Equals(x, query, StringComparison.OrdinalIgnoreCase));
            _items.Insert(0, query);
            if (_items.Count > 40) _items = _items.Take(40).ToList();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathFile)!);
                File.WriteAllText(PathFile, System.Text.Json.JsonSerializer.Serialize(_items));
            }
            catch { }
        }
    }

    private static void Ensure()
    {
        if (_items is not null) return;
        lock (Gate)
        {
            if (_items is not null) return;
            try
            {
                if (File.Exists(PathFile))
                    _items = System.Text.Json.JsonSerializer.Deserialize<List<string>>(File.ReadAllText(PathFile)) ?? [];
            }
            catch { }
            _items ??= [];
        }
    }
}

public static class SmartRanking
{
    public static int Score(IPulseCommand item, string query)
    {
        var q = query.ToLowerInvariant();
        var title = item.Title.ToLowerInvariant();
        var sub = item.Subtitle.ToLowerInvariant();
        var score = 0;

        if (title == q) score += 120;
        else if (title.StartsWith(q)) score += 70;
        else if (title.Contains(q)) score += 45;

        if (sub.Contains(q)) score += 12;

        foreach (var key in item.Keywords)
        {
            var k = key.ToLowerInvariant();
            if (k == q) score += 55;
            else if (k.StartsWith(q) || q.StartsWith(k)) score += 35;
            else if (Fuzzy(k, q) || Fuzzy(title, q)) score += 18;
        }

        if (Fuzzy(title, q)) score += 10;

        // Items with zero textual relevance must never surface, no matter how
        // recently/frequently they were used.
        if (score <= 0) return 0;

        // Pinned / frequent / recent boosts (only for textually relevant items)
        if (ActivityStore.IsFavorite(item.Id)) score += 40;
        score += Math.Min(30, ActivityStore.Frequency(item.Id) * 3);
        var last = ActivityStore.LastUsedUtc(item.Id);
        if (last is DateTime t)
        {
            var hours = (DateTime.UtcNow - t).TotalHours;
            if (hours < 1) score += 25;
            else if (hours < 24) score += 15;
            else if (hours < 168) score += 8;
        }

        // Kind soft preference for settings-like queries
        if (q.Contains("setting") && item.Kind == SearchResultKind.Setting) score += 10;
        if ((q.StartsWith("blu") || q.Contains("bluetooth")) && title.Contains("bluetooth")) score += 40;

        // Recent typed queries boost titles they actually match
        foreach (var recent in SearchHistory.Recent(8))
        {
            if (recent.Length >= 3 && title.Contains(recent, StringComparison.OrdinalIgnoreCase))
                score += 6;
        }

        return score;
    }

    private static bool Fuzzy(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return true;
        var hi = 0;
        foreach (var c in needle)
        {
            hi = haystack.IndexOf(c, hi);
            if (hi < 0) return false;
            hi++;
        }
        return true;
    }
}
