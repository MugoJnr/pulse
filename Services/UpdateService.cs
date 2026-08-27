using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using CpuTempWidget.Models;
using MugoByte.Platform;

namespace CpuTempWidget.Services;

public static class UpdateService
{
    private static int _autoBusy;
    private static int _wired;
    private static UpdateCheckResult? _pending;

    /// <summary>When true (env PULSE_UPDATE_DRY_RUN=1 or --auto-update-dry-run), stage only — no ExitPulse.</summary>
    public static bool DryRunInstall { get; set; } =
        string.Equals(Environment.GetEnvironmentVariable("PULSE_UPDATE_DRY_RUN"), "1", StringComparison.OrdinalIgnoreCase);

    public static void WireBackgroundHost()
    {
        if (Interlocked.Exchange(ref _wired, 1) == 1) return;
        try
        {
            var sync = AppHost.Get<PlatformSyncHost>();
            sync.UpdateDiscovered += OnSyncUpdateDiscovered;
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("UpdateService.WireBackgroundHost", ex);
        }

        // Dev/E2E: mock URL drives auto-install without waiting for Portal.
        if (UpdateE2EOptions.HasMock)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(2500).ConfigureAwait(false);
                    var mock = await UpdateE2EOptions.TryBuildMockResultAsync().ConfigureAwait(false);
                    if (mock is not null)
                        HandleBackgroundUpdate(mock);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.WriteError("UpdateService mock auto", ex);
                }
            });
        }
    }

    private static void OnSyncUpdateDiscovered(UpdateCheckResult update)
    {
        var app = Application.Current;
        if (app?.Dispatcher is null)
        {
            HandleBackgroundUpdate(update);
            return;
        }

        _ = app.Dispatcher.BeginInvoke(() => HandleBackgroundUpdate(update));
    }

    public static async void CheckForUpdates()
    {
        try
        {
            await CheckForUpdatesAsync(interactive: true).ConfigureAwait(true);
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

    /// <summary>Install a pending update discovered by background sync (tray menu).</summary>
    public static async void InstallPendingUpdate()
    {
        try
        {
            var pending = _pending;
            if (pending is null || !pending.UpdateAvailable)
            {
                MessageBox.Show("No update is ready. Use Check for updates.", Branding.ProductName,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            TrayService.ShowBalloon("Pulse update", $"Installing {pending.LatestVersion}…");
            await DownloadAndInstallAsync(pending, silent: true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("UpdateService.InstallPendingUpdate", ex);
        }
    }

    public static void HandleBackgroundUpdate(UpdateCheckResult update)
    {
        if (update is not { UpdateAvailable: true }) return;
        if (string.IsNullOrWhiteSpace(update.LatestVersion) || string.IsNullOrWhiteSpace(update.DownloadUrl))
            return;

        var settings = SettingsService.Load();
        if (!settings.AutoCheckUpdates && !update.IsMandatory)
            return;

        if (string.Equals(settings.LastAutoUpdateVersion, update.LatestVersion, StringComparison.OrdinalIgnoreCase)
            && !settings.AutoInstallUpdates
            && !update.IsMandatory)
            return;

        if (Interlocked.Exchange(ref _autoBusy, 1) == 1) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await ProcessBackgroundAsync(update, settings).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteError("UpdateService.HandleBackgroundUpdate", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _autoBusy, 0);
            }
        });
    }

    private static async Task ProcessBackgroundAsync(UpdateCheckResult update, AppSettings settings)
    {
        var log = AppHost.Get<IPlatformLog>();
        _pending = update;

        settings.LastAutoUpdateVersion = update.LatestVersion;
        SettingsService.Save(settings);

        NotificationCenter.Push(
            "Pulse update available",
            $"Version {update.LatestVersion} is ready.",
            cooldown: TimeSpan.FromHours(6));

        var autoInstall = settings.AutoInstallUpdates || update.IsMandatory || DryRunInstall;
        log.Info("update", autoInstall
            ? $"auto-install {update.LatestVersion}"
            : $"auto-notify {update.LatestVersion} (install from tray or Account)");

        void UiNotify()
        {
            TrayService.ShowBalloon(
                autoInstall ? "Updating Pulse" : "Pulse update available",
                autoInstall
                    ? $"Downloading and installing {update.LatestVersion}…"
                    : $"{update.LatestVersion} is available — tray → Install update");
        }

        try
        {
            var app = Application.Current;
            if (app?.Dispatcher is not null)
                _ = app.Dispatcher.BeginInvoke(UiNotify);
            else
                UiNotify();
        }
        catch { }

        if (!autoInstall) return;

        await DownloadAndInstallAsync(update, silent: true).ConfigureAwait(false);
    }

    private static async Task CheckForUpdatesAsync(bool interactive)
    {
        var opts = AppHost.Get<PlatformOptions>();
        var activation = AppHost.Get<IActivationService>();
        var log = AppHost.Get<IPlatformLog>();

        if (UpdateE2EOptions.HasMock)
        {
            var mock = await UpdateE2EOptions.TryBuildMockResultAsync().ConfigureAwait(true);
            if (mock is not null)
            {
                log.Info("update", "using --mock-update-url override");
                if (interactive)
                    await PromptAndInstallAsync(mock, opts);
                else
                    HandleBackgroundUpdate(mock);
                return;
            }
        }

        var client = AppHost.Get<IPortalUpdateClient>();

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
            if (interactive)
            {
                MessageBox.Show(
                    result.Message ?? "Sign in to check for Pulse updates.",
                    Branding.ProductName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            return;
        }

        if (!result.UpdateAvailable)
        {
            if (interactive)
            {
                MessageBox.Show(
                    result.Message ?? $"Pulse {opts.AppVersion} is up to date.",
                    Branding.ProductName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(result.DownloadUrl) &&
            string.IsNullOrWhiteSpace(result.LatestVersion))
        {
            if (interactive)
            {
                MessageBox.Show(
                    result.Message ?? "No Pulse update is published yet.",
                    Branding.ProductName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            return;
        }

        if (interactive)
            await PromptAndInstallAsync(result, opts);
        else
            HandleBackgroundUpdate(result);
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
            await DownloadAndInstallAsync(result, silent: false);
    }

    /// <summary>Returns false when expected checksum is present and does not match.</summary>
    public static bool VerifyChecksum(byte[] bytes, string? expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256)) return true;
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return hash.Equals(expectedSha256.Trim().ToLowerInvariant(), StringComparison.Ordinal);
    }

    /// <summary>Should background sync auto-install this result given current settings?</summary>
    public static bool ShouldAutoInstall(UpdateCheckResult update, AppSettings settings) =>
        update.UpdateAvailable
        && (settings.AutoInstallUpdates || update.IsMandatory || DryRunInstall)
        && (settings.AutoCheckUpdates || update.IsMandatory);

    private static async Task DownloadAndInstallAsync(UpdateCheckResult update, bool silent)
    {
        if (string.IsNullOrWhiteSpace(update.DownloadUrl))
        {
            if (!silent)
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
                // GitHub private assets may need a token.
                var token = Environment.GetEnvironmentVariable("MBT_GITHUB_TOKEN")
                            ?? Environment.GetEnvironmentVariable("GH_TOKEN")
                            ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
                if (!string.IsNullOrWhiteSpace(token)
                    && url.Contains("github.com", StringComparison.OrdinalIgnoreCase))
                {
                    http.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Trim());
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("MugoByte-Pulse-Updater");
                }
                bytes = await http.GetByteArrayAsync(url);
            }

            if (!VerifyChecksum(bytes, update.ChecksumSha256))
            {
                log.Error("update", "checksum mismatch");
                DiagnosticLog.WriteError("UpdateService checksum mismatch");
                if (!silent)
                {
                    MessageBox.Show("Update integrity check failed. Installation cancelled.", Branding.ProductName,
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else
                {
                    TrayService.ShowBalloon("Pulse update failed", "Integrity check failed.");
                }
                return;
            }

            await File.WriteAllBytesAsync(staged, bytes);
            log.Info("update", "checksum ok — staged " + staged);

            if (DryRunInstall)
            {
                log.Info("update", "auto-update dry-run ready: " + staged);
                DiagnosticLog.WriteError("UpdateService dry-run staged OK: " + staged);
                TrayService.ShowBalloon("Pulse update staged", Path.GetFileName(staged));
                return;
            }

            log.Info("update", "launching installer");
            TrayService.ShowBalloon("Installing Pulse", update.LatestVersion ?? "");
            Process.Start(new ProcessStartInfo(staged) { UseShellExecute = true });
            App.ExitPulse();
        }
        catch (Exception ex)
        {
            log.Error("update", ex.Message);
            DiagnosticLog.WriteError("UpdateService.DownloadAndInstallAsync", ex);
            if (!silent)
            {
                MessageBox.Show("Update download failed:\n" + ex.Message, Branding.ProductName,
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                TrayService.ShowBalloon("Pulse update failed", ex.Message);
            }
        }
    }
}
