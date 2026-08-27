namespace MugoByte.Platform;

/// <summary>Shared configuration for all MugoByte desktop products.</summary>
public sealed class PlatformOptions
{
    public const string DefaultPortalUrl = "https://portal.mugobyte.com";
    public const string PulseProductId = "pulse";

    /// <summary>
    /// Days after last successful cloud check before Pulse requires an online refresh.
    /// Silent session refresh resets this clock; password login is only needed if the
    /// refresh token itself is invalid. Override with MBT_LICENSE_OFFLINE_GRACE_DAYS.
    /// </summary>
    public const int DefaultOfflineGraceDays = 30;

    /// <summary>Portal base URL (override with MBT_PORTAL_URL).</summary>
    public string PortalBaseUrl { get; set; } =
        Environment.GetEnvironmentVariable("MBT_PORTAL_URL")?.TrimEnd('/')
        ?? DefaultPortalUrl;

    public string ProductId { get; set; } = PulseProductId;
    public string ProductDisplayName { get; set; } = "Pulse";
    public string AppVersion { get; set; } = "1.0.0";

    /// <summary>
    /// Offline grace after last successful cloud validation.
    /// Silent reconnect on network restore resets the clock without a password.
    /// </summary>
    public int OfflineGraceDays { get; set; } = DefaultOfflineGraceDays;

    /// <summary>
    /// When true, use in-process mock portal (local validation without live backend).
    /// Also enabled by env MBT_PLATFORM_MODE=mock or arg --mock-account.
    /// Mock still follows Sign in → auto-claim (same process as live).
    /// </summary>
    public bool UseMock { get; set; }

    /// <summary>Cloud validate interval (MBT POS SYNC_INTERVAL = 15 min).</summary>
    public TimeSpan SyncInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Local revalidate / grace tick (MBT POS VALIDATE_INTERVAL = 5 min).</summary>
    public TimeSpan LocalValidateInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Pulse licensing model: any authenticated MugoByte account is entitled to use
    /// the product (free tier). No portal seat reservation or license key required.
    /// Disable with MBT_PULSE_FREE_ACCOUNTS=false to restore POS-style seat claims.
    /// </summary>
    public bool FreeForAccounts { get; set; } =
        !string.Equals(Environment.GetEnvironmentVariable("MBT_PULSE_FREE_ACCOUNTS"), "false", StringComparison.OrdinalIgnoreCase);

    public string PortalAccountUrl => $"{PortalBaseUrl.TrimEnd('/')}/account";
    public string PortalBillingUrl => $"{PortalBaseUrl.TrimEnd('/')}/billing";
    public string PortalDevicesUrl => $"{PortalBaseUrl.TrimEnd('/')}/devices";
    public string PortalDownloadsUrl => $"{PortalBaseUrl.TrimEnd('/')}/downloads";
    public string PortalSupportUrl => $"{PortalBaseUrl.TrimEnd('/')}/support";
    public string PortalRegisterUrl => $"{PortalBaseUrl.TrimEnd('/')}/register";

    public static PlatformOptions ForPulse(string appVersion, bool useMock = false) => new()
    {
        ProductId = PulseProductId,
        ProductDisplayName = "Pulse",
        AppVersion = appVersion,
        OfflineGraceDays = ResolveOfflineGraceDays(),
        UseMock = useMock
            || string.Equals(Environment.GetEnvironmentVariable("MBT_PLATFORM_MODE"), "mock", StringComparison.OrdinalIgnoreCase)
    };

    public static int ResolveOfflineGraceDays()
    {
        var raw = Environment.GetEnvironmentVariable("MBT_LICENSE_OFFLINE_GRACE_DAYS");
        if (int.TryParse(raw, out var days) && days >= 1)
            return days;
        return DefaultOfflineGraceDays;
    }
}
