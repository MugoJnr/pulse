using System.Diagnostics;
using System.Windows;
using MugoByte.Platform;

namespace CpuTempWidget.Services;

/// <summary>
/// Startup account / license gate — mirrors MBT POS launcher.check_license():
/// if not valid → activation UI → exit if still invalid.
/// </summary>
public static class AccountBootstrap
{
    public static bool EnsureReady(bool skipAccount, out LicenseStatus status)
    {
        var guard = AppHost.Get<ILicenseGuard>();
        var opts = AppHost.Get<PlatformOptions>();
        status = guard.Evaluate();

        if (skipAccount)
            return true;

        // Dev-only: repair missing refresh token for --mock-account / mock mode.
        if (opts.UseMock)
            TryRepairMockSession();

        status = guard.Evaluate();

        if (status.State is LicenseState.Unactivated or LicenseState.Tampered
            or LicenseState.DeviceMismatch or LicenseState.SignedOut
            or LicenseState.Blocked)
        {
            var gate = new AccountGateWindow();
            var ok = gate.ShowDialog() == true;
            status = guard.Evaluate();
            return ok && status.AllowsCoreUse
                   && status.State is not LicenseState.Unactivated
                       and not LicenseState.Tampered
                       and not LicenseState.DeviceMismatch;
        }

        // Production: missing refresh token → one-time reconnect UI so user can sign in.
        // Mock path already repaired above; GitHub update fallback still works without portal auth.
        if (!opts.UseMock && NeedsPortalSignInForRefresh())
        {
            var settings = SettingsService.Load();
            if (!settings.PortalSignInPrompted)
            {
                settings.PortalSignInPrompted = true;
                SettingsService.Save(settings);
                status = new LicenseStatus
                {
                    State = status.State,
                    Message = "Portal session needs a fresh sign-in (no refresh token). GitHub updates still work without signing in.",
                    Claims = status.Claims,
                    LastCloudOkUtc = status.LastCloudOkUtc,
                    AllowsCoreUse = status.AllowsCoreUse,
                    RequiresReconnect = true,
                    NeedsAuthRefresh = true
                };
                var reconnect = new AccountReconnectWindow(status);
                _ = reconnect.ShowDialog();
                status = guard.Evaluate();
            }
        }

        // POS offline_lock / expired: must reconnect successfully (no Continue bypass).
        if (status.RequiresReconnect && status.State is LicenseState.GraceExpired or LicenseState.Expired)
        {
            var reconnect = new AccountReconnectWindow(status);
            var ok = reconnect.ShowDialog() == true;
            status = guard.Evaluate();
            return ok && status.AllowsCoreUse;
        }

        return status.AllowsCoreUse || status.State is LicenseState.Active
            or LicenseState.Expiring or LicenseState.GraceWarning;
    }

    private static bool NeedsPortalSignInForRefresh()
    {
        try
        {
            var activation = AppHost.Get<IActivationService>();
            if (!activation.IsActivated) return false;
            var session = activation.CurrentSession;
            if (session is null) return true;
            return string.IsNullOrWhiteSpace(session.RefreshToken);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Mock-only: ensure a refresh token exists so update/auth paths can be exercised offline.</summary>
    private static void TryRepairMockSession()
    {
        try
        {
            var activation = AppHost.Get<IActivationService>();
            var session = activation.CurrentSession;
            if (session is not null && !string.IsNullOrWhiteSpace(session.RefreshToken)
                && !string.IsNullOrWhiteSpace(session.AccessToken))
                return;

            var email = string.IsNullOrWhiteSpace(session?.User?.Email) ? "mock@local" : session!.User.Email;
            _ = activation.SignInAsync(email, "mock-repair-password").GetAwaiter().GetResult();
        }
        catch
        {
            // non-fatal
        }
    }

    public static void StartBackgroundServices()
    {
        try
        {
            var sync = AppHost.Get<PlatformSyncHost>();
            sync.Start();
        }
        catch { }

        try
        {
            UpdateService.WireBackgroundHost();
        }
        catch { }

        try
        {
            _ = AppHost.Get<IActivationService>().TrySilentReconnectAsync();
        }
        catch { }
    }

    public static void OpenPortal(string which)
    {
        var opts = AppHost.Get<PlatformOptions>();
        var url = which.ToLowerInvariant() switch
        {
            "billing" => opts.PortalBillingUrl,
            "devices" => opts.PortalDevicesUrl,
            "downloads" => opts.PortalDownloadsUrl,
            "support" => opts.PortalSupportUrl,
            "register" => opts.PortalRegisterUrl,
            _ => opts.PortalAccountUrl
        };
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }
}
