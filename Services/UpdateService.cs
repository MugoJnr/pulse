using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using MugoByte.Platform;

namespace CpuTempWidget.Services;

public static class UpdateService
{
    public static async void CheckForUpdates()
    {
        try
        {
            await CheckForUpdatesAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("UpdateService.CheckForUpdates", ex);
            try
            {
                MessageBox.Show(
                    "Could not check for updates.\n" + ex.Message,
                    Branding.ProductName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch { }
        }
    }

    private static async Task CheckForUpdatesAsync()
    {
        var opts = AppHost.Get<PlatformOptions>();
        var activation = AppHost.Get<IActivationService>();
        var log = AppHost.Get<IPlatformLog>();

        // E2E / dev mock overrides portal entirely when --mock-update-url is present.
        if (UpdateE2EOptions.HasMock)
        {
            var mock = await UpdateE2EOptions.TryBuildMockResultAsync().ConfigureAwait(true);
            if (mock is not null)
            {
                log.Info("update", "using --mock-update-url override");
                await PromptAndInstallAsync(mock, opts);
                return;
            }
        }

        var client = AppHost.Get<IPortalUpdateClient>();

        // Prefer a fresh bearer token when a stored session exists (same as sync path).
        try { await activation.EnsureFreshSessionAsync().ConfigureAwait(true); }
        catch { /* non-fatal */ }

        var result = await client.CheckAsync(opts.AppVersion).ConfigureAwait(true);
        if (result.NeedsAuthRefresh)
        {
            if (await activation.RefreshSessionAsync().ConfigureAwait(true))
                result = await client.CheckAsync(opts.AppVersion).ConfigureAwait(true);
        }

        if (result.NeedsAuthRefresh)
        {
            // Non-fatal: fall back to GitHub Releases when configured.
            try
            {
                var fallback = AppHost.Get<IUpdateFallback>();
                var gh = await fallback.TryCheckAsync(opts.AppVersion).ConfigureAwait(true);
                if (gh is not null)
                {
                    log.Info("update", "portal unauthorized — using GitHub fallback");
                    result = gh;
                }
            }
            catch (Exception ex)
            {
                log.Warn("update", "GitHub fallback failed: " + ex.Message);
            }
        }

        if (result.NeedsAuthRefresh)
        {
            MessageBox.Show(
                result.Message ?? "Sign in to check for Pulse updates.",
                Branding.ProductName,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!result.UpdateAvailable)
        {
            MessageBox.Show(
                result.Message ?? $"Pulse {opts.AppVersion} is up to date.",
                Branding.ProductName,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(result.DownloadUrl) &&
            string.IsNullOrWhiteSpace(result.LatestVersion))
        {
            MessageBox.Show(
                result.Message ?? "No Pulse update is published yet.",
                Branding.ProductName,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await PromptAndInstallAsync(result, opts);
    }

    private static async Task PromptAndInstallAsync(UpdateCheckResult result, PlatformOptions opts)
    {
        var notes = string.IsNullOrWhiteSpace(result.ReleaseNotes) ? "" : "\n\n" + result.ReleaseNotes;
        var mandatory = result.IsMandatory ? "\n\nThis is a mandatory security update." : "";
        var answer = MessageBox.Show(
            $"Pulse {result.LatestVersion} is available (you have {opts.AppVersion}).{mandatory}{notes}\n\nDownload and install now?",
            Branding.ProductName,
            result.IsMandatory ? MessageBoxButton.OKCancel : MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (answer is MessageBoxResult.Yes or MessageBoxResult.OK)
            await DownloadAndInstallAsync(result);
    }

    /// <summary>Returns false when expected checksum is present and does not match.</summary>
    public static bool VerifyChecksum(byte[] bytes, string? expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256)) return true;
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return hash.Equals(expectedSha256.Trim().ToLowerInvariant(), StringComparison.Ordinal);
    }

    private static async Task DownloadAndInstallAsync(UpdateCheckResult update)
    {
        if (string.IsNullOrWhiteSpace(update.DownloadUrl))
        {
            MessageBox.Show("Update download URL is missing.", Branding.ProductName);
            return;
        }

        var log = AppHost.Get<IPlatformLog>();
        log.Info("update", $"download start {update.LatestVersion}");

        var stageDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MugoByte", "Pulse", "updates");
        Directory.CreateDirectory(stageDir);
        var staged = Path.Combine(stageDir, $"Pulse-Setup-{update.LatestVersion}.exe");

        try
        {
            byte[] bytes;
            var url = update.DownloadUrl;
            if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                || (!url.Contains("://", StringComparison.Ordinal) && File.Exists(url)))
            {
                var path = url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                    ? new Uri(url).LocalPath
                    : url;
                bytes = await File.ReadAllBytesAsync(path);
            }
            else
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                bytes = await http.GetByteArrayAsync(url);
            }

            if (!VerifyChecksum(bytes, update.ChecksumSha256))
            {
                MessageBox.Show("Update integrity check failed. Installation cancelled.", Branding.ProductName,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                log.Error("update", "checksum mismatch");
                DiagnosticLog.WriteError("UpdateService checksum mismatch");
                return;
            }

            await File.WriteAllBytesAsync(staged, bytes);
            log.Info("update", "checksum ok — launching installer");

            // Activation + settings live under %AppData%\MugoByte\Pulse and are preserved.
            Process.Start(new ProcessStartInfo(staged) { UseShellExecute = true });
            App.ExitPulse();
        }
        catch (Exception ex)
        {
            log.Error("update", ex.Message);
            DiagnosticLog.WriteError("UpdateService.DownloadAndInstallAsync", ex);
            MessageBox.Show("Update download failed:\n" + ex.Message, Branding.ProductName,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

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
