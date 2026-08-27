namespace MugoByte.Platform;

/// <summary>In-process portal stand-in for local UI/logic validation without live backend.</summary>
public sealed class MockPortalAuthClient : IPortalAuthClient
{
    private AuthSession? _session;

    public Task<AuthResult> SignInAsync(string email, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return Task.FromResult(new AuthResult { Ok = false, Message = "Email and password are required." });
        if (password.Length < 8)
            return Task.FromResult(new AuthResult { Ok = false, Message = "Password must be at least 8 characters (mock)." });

        _session = new AuthSession
        {
            AccessToken = "mock-access-" + Guid.NewGuid().ToString("N"),
            RefreshToken = "mock-refresh-" + Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(8),
            User = new MugoByteUser
            {
                Id = "mock-user-" + email.GetHashCode().ToString("X"),
                Email = email.Trim(),
                DisplayName = email.Split('@')[0],
                Role = "owner"
            },
            Provider = "mock"
        };
        return Task.FromResult(new AuthResult { Ok = true, Message = "Signed in (demo)", Session = _session });
    }

    public Task<AuthResult> SignUpAsync(string email, string password, string? fullName = null, CancellationToken ct = default)
    {
        if (password.Length < 12)
            return Task.FromResult(new AuthResult { Ok = false, Message = "Password must be at least 12 characters." });
        return SignInAsync(email, password, ct);
    }

    public Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return Task.FromResult(new AuthResult { Ok = false, Message = "No session" });

        _session ??= new AuthSession
        {
            RefreshToken = refreshToken,
            User = new MugoByteUser { Email = "mock@local", DisplayName = "Mock" },
            Provider = "mock"
        };
        _session.AccessToken = "mock-access-" + Guid.NewGuid().ToString("N");
        _session.RefreshToken = refreshToken;
        _session.ExpiresAt = DateTimeOffset.UtcNow.AddHours(8);
        return Task.FromResult(new AuthResult { Ok = true, Session = _session, Message = "refreshed" });
    }

    public Task<MugoByteUser?> GetProfileAsync(string accessToken, CancellationToken ct = default) =>
        Task.FromResult(_session?.User);

    public Task SignOutAsync(CancellationToken ct = default)
    {
        _session = null;
        return Task.CompletedTask;
    }
}

public sealed class MockPortalLicenseClient : IPortalLicenseClient
{
    private readonly PlatformOptions _options;
    private readonly Dictionary<string, DeviceDescriptor> _devices = new(StringComparer.OrdinalIgnoreCase);

    public MockPortalLicenseClient(PlatformOptions options) => _options = options;

    public Task<ActivationResult> ClaimAsync(string accessToken, string deviceId, string fingerprintHash, CancellationToken ct = default) =>
        ActivateAsync(accessToken, deviceId, fingerprintHash, null, ct);

    public Task<ActivationResult> ActivateAsync(string accessToken, string deviceId, string fingerprintHash, string? licenseKey = null, CancellationToken ct = default)
    {
        var type = string.IsNullOrWhiteSpace(licenseKey) ? "trial" : InferType(licenseKey);
        var claims = new ActivationClaims
        {
            UserId = "mock-user",
            UserEmail = "demo@mugobyte.com",
            ProductId = _options.ProductId,
            LicenseType = type,
            PlanId = type,
            PlanDisplayName = char.ToUpperInvariant(type[0]) + type[1..],
            DeviceId = deviceId,
            FingerprintHash = fingerprintHash,
            OfflineGraceDays = _options.OfflineGraceDays,
            MaxDevices = type is "business" or "enterprise" ? 10 : 3,
            ExpiresAt = type == "lifetime" ? null : DateTimeOffset.UtcNow.AddDays(type == "trial" ? 14 : 365),
            // Portal would supply features; mock leaves empty (no client-invented map).
            Features = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        };
        var token = new ActivationToken
        {
            Claims = claims,
            Issuer = "mugobyte-mock",
            IssuedAt = DateTimeOffset.UtcNow,
            Signature = ActivationCrypto.Sign(claims, fingerprintHash, _options.ProductId)
        };
        _devices[deviceId] = new DeviceDescriptor
        {
            DeviceId = deviceId,
            DisplayName = Environment.MachineName,
            FingerprintHash = fingerprintHash,
            AppVersion = _options.AppVersion,
            OsVersion = Environment.OSVersion.VersionString,
            ActivatedAt = DateTimeOffset.UtcNow,
            LastOnlineAt = DateTimeOffset.UtcNow
        };
        return Task.FromResult(new ActivationResult
        {
            Ok = true,
            Message = $"Device activated from your account. Plan: {claims.PlanDisplayName}",
            Token = token,
            Plan = new LicensePlanInfo
            {
                PlanId = type,
                DisplayName = claims.PlanDisplayName,
                LicenseType = type,
                MaxDevices = claims.MaxDevices,
                ExpiresAt = claims.ExpiresAt,
                Features = claims.Features
            }
        });
    }

    public Task<LicenseStatus> ValidateAsync(string accessToken, string deviceId, string fingerprintHash, CancellationToken ct = default) =>
        Task.FromResult(new LicenseStatus
        {
            State = LicenseState.Active,
            Message = "Demo portal validation ok",
            LastCloudOkUtc = DateTimeOffset.UtcNow,
            AllowsCoreUse = true
        });

    public Task<IReadOnlyList<DeviceDescriptor>> ListDevicesAsync(string accessToken, CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyList<DeviceDescriptor>)_devices.Values.ToList());

    public Task<PlatformResult> DeactivateDeviceAsync(string accessToken, string deviceId, CancellationToken ct = default)
    {
        _devices.Remove(deviceId);
        return Task.FromResult(PlatformResult.Success("Deactivated"));
    }

    private static string InferType(string key)
    {
        key = key.ToUpperInvariant();
        if (key.Contains("LIFE")) return "lifetime";
        if (key.Contains("ENT")) return "enterprise";
        if (key.Contains("BUS")) return "business";
        if (key.Contains("YEAR") || key.Contains("ANN")) return "yearly";
        if (key.Contains("MON")) return "monthly";
        return "trial";
    }
}

public sealed class MockPortalUpdateClient : IPortalUpdateClient
{
    private readonly PlatformOptions _options;
    public MockPortalUpdateClient(PlatformOptions options) => _options = options;

    public Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken ct = default) =>
        Task.FromResult(new UpdateCheckResult
        {
            UpdateAvailable = false,
            LatestVersion = currentVersion,
            Message = $"Demo update channel: Pulse {_options.AppVersion} is current."
        });
}
