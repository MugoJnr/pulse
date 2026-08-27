using System.IO;
using System.Text.Json;
using CpuTempWidget.Models;
using CpuTempWidget.Services;

namespace CpuTempWidget.Core;

public sealed class RecentEntry
{
    public string CommandId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Glyph { get; set; } = "";
    public DateTime Utc { get; set; }
    public int Count { get; set; } = 1;
}

public sealed class FavoriteEntry
{
    public string CommandId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Glyph { get; set; } = "";
    public string Subtitle { get; set; } = "";
}

public static class ActivityStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static List<RecentEntry>? _recent;
    private static List<FavoriteEntry>? _favorites;

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MugoByte", "Pulse");

    private static string RecentPath => Path.Combine(Dir, "recent.json");
    private static string FavoritesPath => Path.Combine(Dir, "favorites.json");
    private static string LogPath => Path.Combine(Dir, "actions.log");

    public static IReadOnlyList<RecentEntry> GetRecent(int limit = 30)
    {
        EnsureRecent();
        return _recent!.OrderByDescending(x => x.Utc).Take(limit).ToList();
    }

    public static IReadOnlyList<FavoriteEntry> GetFavorites()
    {
        EnsureFavorites();
        return _favorites!;
    }

    public static bool IsFavorite(string commandId)
    {
        EnsureFavorites();
        return _favorites!.Any(f => f.CommandId == commandId);
    }

    public static void Record(IPulseCommand cmd)
    {
        EnsureRecent();
        var recent = _recent ?? [];
        var existing = recent.FirstOrDefault(x => x.CommandId == cmd.Id);
        if (existing is null)
        {
            recent.Insert(0, new RecentEntry
            {
                CommandId = cmd.Id,
                Title = cmd.Title,
                Glyph = cmd.Glyph,
                Utc = DateTime.UtcNow,
                Count = 1
            });
        }
        else
        {
            existing.Utc = DateTime.UtcNow;
            existing.Count++;
            existing.Title = cmd.Title;
            existing.Glyph = cmd.Glyph;
        }

        _recent = recent.OrderByDescending(x => x.Utc).Take(80).ToList();
        Save(RecentPath, _recent);
        AppendLog(cmd.Id, cmd.Title, "ok");
    }

    public static void ToggleFavorite(IPulseCommand cmd)
    {
        EnsureFavorites();
        var favorites = _favorites ?? [];
        var existing = favorites.FirstOrDefault(f => f.CommandId == cmd.Id);
        if (existing is null)
        {
            favorites.Add(new FavoriteEntry
            {
                CommandId = cmd.Id,
                Title = cmd.Title,
                Glyph = cmd.Glyph,
                Subtitle = cmd.Subtitle
            });
        }
        else
        {
            favorites.Remove(existing);
        }
        _favorites = favorites;
        Save(FavoritesPath, favorites);
    }

    public static int Frequency(string commandId)
    {
        EnsureRecent();
        return _recent!.FirstOrDefault(x => x.CommandId == commandId)?.Count ?? 0;
    }

    public static DateTime? LastUsedUtc(string commandId)
    {
        EnsureRecent();
        return _recent!.FirstOrDefault(x => x.CommandId == commandId)?.Utc;
    }

    public static void AppendLog(string id, string title, string result)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {title} ({id}) — {result}\n");
        }
        catch { }
    }

    private static void EnsureRecent()
    {
        if (_recent is not null) return;
        _recent = Load<List<RecentEntry>>(RecentPath) ?? [];
    }

    private static void EnsureFavorites()
    {
        if (_favorites is not null) return;
        _favorites = Load<List<FavoriteEntry>>(FavoritesPath) ?? [];
    }

    private static T? Load<T>(string path)
    {
        try
        {
            if (!File.Exists(path)) return default;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
        }
        catch { return default; }
    }

    private static void Save<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
        }
        catch { }
    }
}

public static class SafetyService
{
    private static readonly HashSet<string> AlwaysConfirmIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "maint.clear-prefetch", "maint.winsock", "maint.sfc", "maint.dism",
        "maint.empty-recycle", "apps.kill-tree", "power.shutdown", "power.restart-pc"
    };

    public static bool ShouldConfirm(IPulseCommand cmd)
    {
        var settings = SettingsService.Load();
        if (!settings.ConfirmDestructiveActions) return false;
        return cmd.IsDestructive || AlwaysConfirmIds.Contains(cmd.Id);
    }

    public static bool Confirm(IPulseCommand cmd)
    {
        if (!ShouldConfirm(cmd)) return true;
        var result = System.Windows.MessageBox.Show(
            $"Run “{cmd.Title}”?\n\n{cmd.Subtitle}",
            Branding.ProductName,
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning);
        return result == System.Windows.MessageBoxResult.OK;
    }
}

public static class CommandDispatcher
{
    public static bool Execute(IPulseCommand command)
    {
        try
        {
            if (command.RequiresElevation && !AdminLauncher.IsElevated)
            {
                var go = System.Windows.MessageBox.Show(
                    $"“{command.Title}” works best as Administrator.\n\nContinue anyway?",
                    Branding.ProductName,
                    System.Windows.MessageBoxButton.OKCancel,
                    System.Windows.MessageBoxImage.Information);
                if (go != System.Windows.MessageBoxResult.OK)
                {
                    ActivityStore.AppendLog(command.Id, command.Title, "cancelled-elevation");
                    return false;
                }
            }

            if (!SafetyService.Confirm(command))
            {
                ActivityStore.AppendLog(command.Id, command.Title, "cancelled");
                return false;
            }

            command.Execute();
            ActivityStore.Record(command);
            return true;
        }
        catch (Exception ex)
        {
            ActivityStore.AppendLog(command.Id, command.Title, "error:" + ex.Message);
            return false;
        }
    }
}
