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
        status = guard.Evaluate();

        if (skipAccount)
            return true;

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
