namespace MugoByte.Platform;

public interface IPortalAuthClient
{
    Task<AuthResult> SignInAsync(string email, string password, CancellationToken ct = default);
    Task<AuthResult> SignUpAsync(string email, string password, string? fullName = null, CancellationToken ct = default);
    Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken ct = default);
    Task<MugoByteUser?> GetProfileAsync(string accessToken, CancellationToken ct = default);
    Task SignOutAsync(CancellationToken ct = default);
}

public interface IPortalLicenseClient
{
    Task<ActivationResult> ClaimAsync(string accessToken, string deviceId, string fingerprintHash, CancellationToken ct = default);
    Task<ActivationResult> ActivateAsync(string accessToken, string deviceId, string fingerprintHash, string? licenseKey = null, CancellationToken ct = default);
    Task<LicenseStatus> ValidateAsync(string accessToken, string deviceId, string fingerprintHash, CancellationToken ct = default);
    Task<IReadOnlyList<DeviceDescriptor>> ListDevicesAsync(string accessToken, CancellationToken ct = default);
    Task<PlatformResult> DeactivateDeviceAsync(string accessToken, string deviceId, CancellationToken ct = default);
}

public interface IPortalUpdateClient
{
    Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken ct = default);
}

/// <summary>
/// Optional secondary update source (e.g. GitHub Releases) when the portal check is unauthorized.
/// </summary>
public interface IUpdateFallback
{
    Task<UpdateCheckResult?> TryCheckAsync(string currentVersion, CancellationToken ct = default);
}

public interface IPlatformSync
{
    Task<SyncSnapshot> SynchronizeAsync(CancellationToken ct = default);
}

public interface ILicenseGuard
{
    LicenseStatus Evaluate();
    bool HasFeature(string featureId);
    event Action<LicenseStatus>? StateChanged;
}

public interface IActivationService
{
    Task<AuthResult> SignInAsync(string email, string password, CancellationToken ct = default);
    Task<AuthResult> SignUpAsync(string email, string password, string? fullName, CancellationToken ct = default);
    /// <summary>
    /// MBT POS hybrid flow: sign in → silent seat claim → optional license key fallback.
    /// </summary>
    Task<ActivationResult> SignInAndActivateAsync(string email, string password, string? licenseKey = null, CancellationToken ct = default);
    Task<ActivationResult> ActivateCurrentDeviceAsync(string? licenseKey = null, CancellationToken ct = default);
    /// <summary>Mock-only shortcut. Prefer <see cref="SignInAndActivateAsync"/> (same process).</summary>
    Task<ActivationResult> ActivateDemoAsync(CancellationToken ct = default);
    Task SignOutAsync(CancellationToken ct = default);
    Task RefreshLicenseAsync(CancellationToken ct = default);
    /// <summary>
    /// Refresh the stored session (if needed) and validate with the portal.
    /// Does not prompt for a password. Returns true when LastCloudOkUtc was updated.
    /// </summary>
    Task<bool> TrySilentReconnectAsync(CancellationToken ct = default);
    /// <summary>
    /// Ensure access token is not near expiry; refresh via stored refresh token if needed.
    /// Returns false when there is no usable session.
    /// </summary>
    Task<bool> EnsureFreshSessionAsync(CancellationToken ct = default);
    /// <summary>
    /// Force a session refresh using the stored refresh token (e.g. after a portal 401).
    /// </summary>
    Task<bool> RefreshSessionAsync(CancellationToken ct = default);
    AuthSession? CurrentSession { get; }
    StoredActivation? CurrentActivation { get; }
    DeviceDescriptor CurrentDevice { get; }
    bool IsSignedIn { get; }
    bool IsActivated { get; }
}

public interface IPlatformLog
{
    void Info(string category, string message);
    void Warn(string category, string message);
    void Error(string category, string message);
}
