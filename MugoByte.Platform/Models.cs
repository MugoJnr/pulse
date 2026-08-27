using System.Text.Json.Serialization;

namespace MugoByte.Platform;

public sealed class MugoByteUser
{
    public string Id { get; set; } = "";
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? AvatarUrl { get; set; }
    public string? Role { get; set; }
}

public sealed class AuthSession
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTimeOffset? ExpiresAt { get; set; }
    public MugoByteUser User { get; set; } = new();
    public string Provider { get; set; } = "supabase";
}

public sealed class LicensePlanInfo
{
    public string PlanId { get; set; } = "trial";
    public string DisplayName { get; set; } = "Trial";
    public string LicenseType { get; set; } = "trial"; // trial|monthly|yearly|lifetime|business|enterprise
    public int MaxDevices { get; set; } = 1;
    public DateTimeOffset? ExpiresAt { get; set; }
    public Dictionary<string, bool> Features { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Limits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DeviceDescriptor
{
    public string DeviceId { get; set; } = "";
    public string DisplayName { get; set; } = Environment.MachineName;
    public string OsVersion { get; set; } = Environment.OSVersion.VersionString;
    public string FingerprintHash { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public DateTimeOffset ActivatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastOnlineAt { get; set; }
}

public sealed class ActivationClaims
{
    public string TokenId { get; set; } = Guid.NewGuid().ToString("N");
    public string UserId { get; set; } = "";
    public string UserEmail { get; set; } = "";
    public string ProductId { get; set; } = PlatformOptions.PulseProductId;
    public string LicenseType { get; set; } = "trial";
    public string PlanId { get; set; } = "trial";
    public string PlanDisplayName { get; set; } = "Trial";
    public DateTimeOffset ActivatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public string DeviceId { get; set; } = "";
    public string FingerprintHash { get; set; } = "";
    public string MinAppVersion { get; set; } = "1.0.0";
    public int OfflineGraceDays { get; set; } = PlatformOptions.DefaultOfflineGraceDays;
    public int MaxDevices { get; set; } = 1;
    public Dictionary<string, bool> Features { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Limits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Signed activation envelope. Signature covers the canonical JSON of Claims.</summary>
public sealed class ActivationToken
{
    public ActivationClaims Claims { get; set; } = new();
    public string Signature { get; set; } = "";
    public string Issuer { get; set; } = "mugobyte-portal";
    public DateTimeOffset IssuedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class StoredActivation
{
    public ActivationToken Token { get; set; } = new();
    public DateTimeOffset LastCloudOkUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset StoredAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string SchemaVersion { get; set; } = "1";
}

public sealed class LicenseStatus
{
    public LicenseState State { get; set; } = LicenseState.Unactivated;
    public string Message { get; set; } = "";
    public ActivationClaims? Claims { get; set; }
    public DateTimeOffset? LastCloudOkUtc { get; set; }
    public int GraceDaysRemaining { get; set; }
    public bool IsOffline { get; set; }
    public bool RequiresReconnect { get; set; }
    public bool AllowsCoreUse { get; set; } = true;
    /// <summary>Portal returned 401/403 — refresh the session token and retry.</summary>
    public bool NeedsAuthRefresh { get; set; }
    /// <summary>License validate route missing (404/405/501) — fall back to session verification.</summary>
    public bool EndpointMissing { get; set; }
}

public enum LicenseState
{
    Unactivated,
    Active,
    Expiring,
    GraceWarning,
    GraceExpired,
    Expired,
    Tampered,
    DeviceMismatch,
    SignedOut,
    /// <summary>Server-authoritative: this device was revoked/blocked in the Portal.</summary>
    Blocked
}

public sealed class UpdateCheckResult
{
    public bool UpdateAvailable { get; set; }
    public string? LatestVersion { get; set; }
    public string? DownloadUrl { get; set; }
    public string? ChecksumSha256 { get; set; }
    public string? ReleaseNotes { get; set; }
    public bool IsMandatory { get; set; }
    public string? Message { get; set; }
    /// <summary>Portal returned 401/403 — refresh the session and retry (non-fatal).</summary>
    public bool NeedsAuthRefresh { get; set; }
}

public sealed class SyncSnapshot
{
    public DateTimeOffset SyncedAtUtc { get; set; }
    public LicenseStatus? License { get; set; }
    public IReadOnlyList<string> Announcements { get; set; } = [];
    public Dictionary<string, bool> FeatureFlags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public UpdateCheckResult? Update { get; set; }
}

/// <summary>Durable, signed-in-time server verdict about this device (revocation etc.).</summary>
public sealed class ServerVerdict
{
    public bool Blocked { get; set; }
    public string Message { get; set; } = "";
    public DateTimeOffset Utc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PlatformResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public static PlatformResult Success(string message = "ok") => new() { Ok = true, Message = message };
    public static PlatformResult Fail(string message) => new() { Ok = false, Message = message };
}

public sealed class AuthResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public AuthSession? Session { get; init; }
    public bool VerificationRequired { get; init; }
}

public sealed class ActivationResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public ActivationToken? Token { get; init; }
    public LicensePlanInfo? Plan { get; init; }
}
