using System.Net.NetworkInformation;
using System.Text;

namespace MugoByte.Platform;

public sealed class FilePlatformLog : IPlatformLog
{
    private readonly string _path;
    private readonly object _gate = new();

    public FilePlatformLog(string productFolder)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MugoByte", productFolder);
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "platform.log");
    }

    public void Info(string category, string message) => Write("INFO", category, message);
    public void Warn(string category, string message) => Write("WARN", category, message);
    public void Error(string category, string message) => Write("ERROR", category, message);

    private void Write(string level, string category, string message)
    {
        // Never log secrets — caller must not pass tokens/passwords.
        var line = $"[{DateTimeOffset.Now:O}] {level} {category}: {Sanitize(message)}\n";
        lock (_gate)
        {
            try { File.AppendAllText(_path, line); }
            catch { }
        }
    }

    private static string Sanitize(string message)
    {
        if (string.IsNullOrEmpty(message)) return message;
        // Redact obvious token-like substrings
        return System.Text.RegularExpressions.Regex.Replace(
            message,
            @"(Bearer\s+)?[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}",
            "[redacted]");
    }
}

public sealed class IdentityStore
{
    private const string SessionKey = "auth_session";
    private const string ActivationKey = "activation";
    private readonly ISecureStore _store;

    public IdentityStore(ISecureStore store) => _store = store;

    public AuthSession? LoadSession()
    {
        var bytes = _store.Load(SessionKey);
        if (bytes is null) return null;
        return PlatformJson.Deserialize<AuthSession>(bytes);
    }

    public void SaveSession(AuthSession session) =>
        _store.Save(SessionKey, Encoding.UTF8.GetBytes(PlatformJson.Serialize(session)));

    public void ClearSession() => _store.Delete(SessionKey);

    public StoredActivation? LoadActivation()
    {
        var bytes = _store.Load(ActivationKey);
        if (bytes is null) return null;
        return PlatformJson.Deserialize<StoredActivation>(bytes);
    }

    public void SaveActivation(StoredActivation activation) =>
        _store.Save(ActivationKey, Encoding.UTF8.GetBytes(PlatformJson.Serialize(activation)));

    public void ClearActivation() => _store.Delete(ActivationKey);

    private const string VerdictKey = "server_verdict";

    /// <summary>Last confirmed server licensing verdict (e.g., device blocked).</summary>
    public ServerVerdict? LoadServerVerdict()
    {
        var bytes = _store.Load(VerdictKey);
        return bytes is null ? null : PlatformJson.Deserialize<ServerVerdict>(bytes);
    }

    public void SaveServerVerdict(ServerVerdict verdict) =>
        _store.Save(VerdictKey, Encoding.UTF8.GetBytes(PlatformJson.Serialize(verdict)));

    public void ClearServerVerdict() => _store.Delete(VerdictKey);
}

public sealed class ActivationService : IActivationService
{
    private readonly PlatformOptions _options;
    private readonly IPortalAuthClient _auth;
    private readonly IPortalLicenseClient _license;
    private readonly IdentityStore _identity;
    private readonly ISecureStore _secure;
    private readonly IPlatformLog _log;
    private static DateTimeOffset _lastNoRefreshLogUtc = DateTimeOffset.MinValue;
    private readonly ILicenseGuard _guard;
    private readonly string _fingerprint;
    private readonly string _deviceId;
    private static DateTimeOffset _lastGraceRefreshUtc = DateTimeOffset.MinValue;

    public ActivationService(
        PlatformOptions options,
        IPortalAuthClient auth,
        IPortalLicenseClient license,
        IdentityStore identity,
        ISecureStore secure,
        IPlatformLog log,
        ILicenseGuard guard)
    {
        _options = options;
        _auth = auth;
        _license = license;
        _identity = identity;
        _secure = secure;
        _log = log;
        _guard = guard;
        _fingerprint = DeviceFingerprint.ComputeHash();
        _deviceId = DeviceFingerprint.GetOrCreateDeviceId(secure, "PULSE");
        CurrentSession = identity.LoadSession();
        CurrentActivation = identity.LoadActivation();
    }

    public AuthSession? CurrentSession { get; private set; }
    public StoredActivation? CurrentActivation { get; private set; }
    public bool IsSignedIn => CurrentSession is not null && !string.IsNullOrWhiteSpace(CurrentSession.AccessToken);
    public bool IsActivated => CurrentActivation?.Token is not null;

    public DeviceDescriptor CurrentDevice => new()
    {
        DeviceId = _deviceId,
        DisplayName = Environment.MachineName,
        OsVersion = Environment.OSVersion.VersionString,
        FingerprintHash = _fingerprint,
        AppVersion = _options.AppVersion,
        ActivatedAt = CurrentActivation?.Token.Claims.ActivatedAt ?? DateTimeOffset.UtcNow,
        LastOnlineAt = CurrentActivation?.LastCloudOkUtc
    };

    public async Task<AuthResult> SignInAsync(string email, string password, CancellationToken ct = default)
    {
        var result = await _auth.SignInAsync(email, password, ct);
        if (!result.Ok || result.Session is null) return result;
        if (string.IsNullOrWhiteSpace(result.Session.RefreshToken))
            _log.Warn("auth", "portal returned no refresh_token");
        if (result.Session.ExpiresAt is null
            && AccessTokenExpiry.TryReadExp(result.Session.AccessToken, out var jwtExp))
            result.Session.ExpiresAt = jwtExp;
        CurrentSession = result.Session;
        _identity.SaveSession(result.Session);
        _log.Info("auth", $"signed in as {result.Session.User.Email}");
        return result;
    }

    public async Task<AuthResult> SignUpAsync(string email, string password, string? fullName, CancellationToken ct = default)
    {
        var result = await _auth.SignUpAsync(email, password, fullName, ct);
        if (result is { Ok: true, Session: not null })
        {
            CurrentSession = result.Session;
            _identity.SaveSession(result.Session);
        }
        _log.Info("auth", "sign-up " + (result.Ok ? "ok" : "failed"));
        return result;
    }

    public async Task<ActivationResult> SignInAndActivateAsync(
        string email, string password, string? licenseKey = null, CancellationToken ct = default)
    {
        // Mirrors MBT POS cloud_onboarding.auto_claim_device_license:
        // login → claim seat silently → manual key only when claim fails / key provided.
        var auth = await SignInAsync(email, password, ct);
        if (!auth.Ok)
            return new ActivationResult { Ok = false, Message = auth.Message };

        if (auth.VerificationRequired && auth.Session is null)
            return new ActivationResult
            {
                Ok = false,
                Message = auth.Message + " Then sign in to activate."
            };

        if (!string.IsNullOrWhiteSpace(licenseKey))
            return await ActivateCurrentDeviceAsync(licenseKey, ct);

        var claimed = await ActivateCurrentDeviceAsync(null, ct);
        if (claimed.Ok)
            return claimed;

        // Pulse free-tier: any authenticated account is entitled to use the product.
        // A portal seat is not required; mint a device-bound local free activation.
        if (_options.FreeForAccounts)
            return await ProvisionFreeActivationAsync(ct);

        // POS-style fallback messaging when no seat / org / claim path fails.
        return new ActivationResult
        {
            Ok = false,
            Message = string.IsNullOrWhiteSpace(claimed.Message)
                ? "Signed in, but no license seat is available for this device. Enter a license key or free a seat in the Portal."
                : claimed.Message + " You can paste a license key below instead."
        };
    }

    /// <summary>
    /// Free-tier activation bound to the signed-in account + this device. Signed with the
    /// same local HMAC scheme so LicenseGuard offline verification passes without a seat.
    /// </summary>
    public async Task<ActivationResult> ProvisionFreeActivationAsync(CancellationToken ct = default)
    {
        if (!IsSignedIn || CurrentSession is null)
            return new ActivationResult { Ok = false, Message = "Sign in to your MugoByte account first." };

        var claims = new ActivationClaims
        {
            UserId = CurrentSession.User?.Id ?? "",
            UserEmail = CurrentSession.User?.Email ?? "",
            ProductId = _options.ProductId,
            LicenseType = "free",
            PlanId = "free",
            PlanDisplayName = "Pulse Free",
            DeviceId = _deviceId,
            FingerprintHash = _fingerprint,
            MaxDevices = int.MaxValue,
            OfflineGraceDays = _options.OfflineGraceDays,
            MinAppVersion = "1.0.0",
            Features = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        };

        var token = new ActivationToken
        {
            Claims = claims,
            Issuer = "mugobyte-account-free",
            IssuedAt = DateTimeOffset.UtcNow,
            Signature = ActivationCrypto.Sign(claims, _fingerprint, _options.ProductId)
        };

        await Task.CompletedTask;
        _log.Info("license", "free-tier activation provisioned from account");
        return PersistToken(new ActivationResult
        {
            Ok = true,
            Message = $"Activated — {claims.PlanDisplayName} (MugoByte account)",
            Token = token
        });
    }

    public async Task<ActivationResult> ActivateCurrentDeviceAsync(string? licenseKey = null, CancellationToken ct = default)
    {
        if (!IsSignedIn || CurrentSession is null)
            return new ActivationResult { Ok = false, Message = "Sign in to your MugoByte account first." };

        ActivationResult result;
        if (string.IsNullOrWhiteSpace(licenseKey))
            result = await _license.ClaimAsync(CurrentSession.AccessToken, _deviceId, _fingerprint, ct);
        else
            result = await _license.ActivateAsync(CurrentSession.AccessToken, _deviceId, _fingerprint, licenseKey, ct);

        if (!result.Ok || result.Token is null)
            return result;

        return PersistToken(result);
    }

    public Task<ActivationResult> ActivateDemoAsync(CancellationToken ct = default)
    {
        // Mock-only escape hatch. Production UX uses Sign In & Activate (claim) like MBT POS.
        if (!_options.UseMock)
        {
            return Task.FromResult(new ActivationResult
            {
                Ok = false,
                Message = "Demo activation is disabled. Sign in with your MugoByte account (or run with --mock-account)."
            });
        }

        return ActivateCurrentDeviceAsync(null, ct);
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        try { await _auth.SignOutAsync(ct); } catch { }
        CurrentSession = null;
        CurrentActivation = null;
        _identity.ClearSession();
        _identity.ClearActivation();
        _log.Info("auth", "signed out — activation cleared");
        _ = _guard.Evaluate();
    }

    public async Task RefreshLicenseAsync(CancellationToken ct = default) =>
        await TrySilentReconnectAsync(ct);

    public async Task<bool> TrySilentReconnectAsync(CancellationToken ct = default)
    {
        // Free tier: signed-in device without stored activation provisions automatically.
        if (!IsActivated)
        {
            if (_options.FreeForAccounts && await EnsureFreshSessionAsync(ct) && CurrentSession is not null)
            {
                var probe = await _auth.GetProfileAsync(CurrentSession.AccessToken, ct);
                if (probe is not null)
                    return (await ProvisionFreeActivationAsync(ct)).Ok;
            }
            return false;
        }

        if (!NetworkState.IsAvailable())
        {
            _log.Info("license", "silent reconnect skipped — no network");
            return false;
        }

        if (!await EnsureFreshSessionAsync(ct))
        {
            _log.Warn("license", "silent reconnect: session refresh failed");
            _guard.Evaluate();
            return false;
        }

        var device = CurrentDevice;
        var status = await _license.ValidateAsync(CurrentSession!.AccessToken, device.DeviceId, device.FingerprintHash, ct);
        if (status.NeedsAuthRefresh)
        {
            if (await RefreshSessionAsync(ct))
                status = await _license.ValidateAsync(CurrentSession.AccessToken, device.DeviceId, device.FingerprintHash, ct);
        }

        // Confirmed server-side revocation — persist so local evaluation enforces it.
        if (status.State == LicenseState.Blocked)
        {
            _identity.SaveServerVerdict(new ServerVerdict
            {
                Blocked = true,
                Message = status.Message,
                Utc = DateTimeOffset.UtcNow
            });
            _log.Warn("license", "server verdict: device blocked/revoked");
            _guard.Evaluate();
            return false;
        }

        if (status.State == LicenseState.Active && !status.IsOffline && !status.NeedsAuthRefresh)
        {
            _identity.ClearServerVerdict();
            var stored = _identity.LoadActivation() ?? CurrentActivation;
            if (stored is not null)
            {
                stored.LastCloudOkUtc = DateTimeOffset.UtcNow;
                _identity.SaveActivation(stored);
                CurrentActivation = stored;
            }
            _log.Info("license", "cloud refresh ok (silent)");
            _guard.Evaluate();
            return true;
        }

        // Fallback: portal build without a deployed /licenses/validate route.
        // Verify connectivity + session via the auth profile endpoint so a connected
        // machine is never soft-locked by a server-side deployment gap. When the
        // validate route ships, the normal path takes over automatically.
        if (status.EndpointMissing && !status.IsOffline && CurrentSession is not null)
        {
            // Throttle: sync/network events can call this every few seconds.
            if (DateTimeOffset.UtcNow - _lastGraceRefreshUtc < TimeSpan.FromSeconds(60))
                return true;

            var profile = await _auth.GetProfileAsync(CurrentSession.AccessToken, ct);
            if (profile is null && await RefreshSessionAsync(ct))
            {
                _log.Info("license", "access token stale — refreshed via session endpoint");
                profile = await _auth.GetProfileAsync(CurrentSession!.AccessToken, ct);
            }

            if (profile is not null)
            {
                var fallbackStored = _identity.LoadActivation() ?? CurrentActivation;
                if (fallbackStored is not null)
                {
                    fallbackStored.LastCloudOkUtc = DateTimeOffset.UtcNow;
                    _identity.SaveActivation(fallbackStored);
                    CurrentActivation = fallbackStored;
                }
                _lastGraceRefreshUtc = DateTimeOffset.UtcNow;
                _log.Warn("license", "validate route missing — grace refreshed via session check");
                _guard.Evaluate();
                return true;
            }

            _log.Warn("license", "session invalid — sign-in required to refresh license");

            // Free tier: never strand an already-verified device behind a dead session.
            // Re-bind the locally verified activation to the free plan.
            if (_options.FreeForAccounts && MigrateStoredActivationToFreeTier())
            {
                _guard.Evaluate();
                return true;
            }
        }

        _log.Warn("license", "cloud refresh: " + status.Message);
        _guard.Evaluate();
        return false;
    }

    public async Task<bool> EnsureFreshSessionAsync(CancellationToken ct = default)
    {
        CurrentSession ??= _identity.LoadSession();
        if (CurrentSession is null || string.IsNullOrWhiteSpace(CurrentSession.AccessToken))
            return false;

        // Prefer stored ExpiresAt; if missing, decode JWT exp as a non-authoritative hint.
        var exp = CurrentSession.ExpiresAt;
        if (exp is null
            && AccessTokenExpiry.TryReadExp(CurrentSession.AccessToken, out var jwtExp))
        {
            exp = jwtExp;
            CurrentSession.ExpiresAt = jwtExp;
            try { _identity.SaveSession(CurrentSession); } catch { }
        }

        // Still unknown: keep token and let 401 callers force-refresh.
        if (exp is null)
            return true;
        if (exp > DateTimeOffset.UtcNow.AddMinutes(2))
            return true;

        return await RefreshSessionAsync(ct);
    }

    public async Task<bool> RefreshSessionAsync(CancellationToken ct = default)
    {
        CurrentSession ??= _identity.LoadSession();
        var refresh = CurrentSession?.RefreshToken;
        if (string.IsNullOrWhiteSpace(refresh))
        {
            // Access-only / empty-refresh sessions cannot renew — clear when expired so AccountGate can appear.
            ClearExpiredAccessOnlySession();
            if (DateTimeOffset.UtcNow - _lastNoRefreshLogUtc > TimeSpan.FromHours(1))
            {
                _lastNoRefreshLogUtc = DateTimeOffset.UtcNow;
                _log.Info("auth", "no refresh token stored — sign in again to renew session (non-fatal)");
            }
            return false;
        }

        var result = await _auth.RefreshAsync(refresh, ct);
        if (!result.Ok || result.Session is null)
        {
            _log.Info("auth", "session refresh failed — sign in again when convenient (non-fatal)");
            return false;
        }

        CurrentSession = result.Session;
        if (string.IsNullOrWhiteSpace(CurrentSession.RefreshToken))
            CurrentSession.RefreshToken = refresh;
        _identity.SaveSession(CurrentSession);
        _log.Info("auth", "session refreshed silently");
        return true;
    }

    /// <summary>
    /// Removes expired access-token-only sessions (no refreshToken) so AccountGate can prompt sign-in.
    /// Never touches activation.bin.
    /// </summary>
    private void ClearExpiredAccessOnlySession()
    {
        try
        {
            var session = CurrentSession ?? _identity.LoadSession();
            if (session is null) return;
            if (!string.IsNullOrWhiteSpace(session.RefreshToken)) return;

            var exp = session.ExpiresAt;
            if (exp is null
                && !string.IsNullOrWhiteSpace(session.AccessToken)
                && AccessTokenExpiry.TryReadExp(session.AccessToken, out var jwtExp))
                exp = jwtExp;

            // Unknown expiry with no refresh is unusable for renewal — clear it.
            if (exp is null || exp <= DateTimeOffset.UtcNow)
            {
                _identity.ClearSession();
                CurrentSession = null;
                _log.Info("auth", "cleared expired access-only session — sign in required (activation preserved)");
            }
        }
        catch
        {
            // non-fatal
        }
    }

    private bool MigrateStoredActivationToFreeTier()
    {
        var stored = _identity.LoadActivation() ?? CurrentActivation;
        var tok = stored?.Token;
        if (tok is null) return false;

        var matchedFp = DeviceFingerprint.VerifyWithCompatibleFingerprints(tok, _options.ProductId);
        if (matchedFp is null)
            return false;

        // Re-bind to current v2 fingerprint for new activations going forward.
        tok.Claims.LicenseType = "free";
        tok.Claims.PlanId = "free";
        tok.Claims.PlanDisplayName = "Pulse Free";
        tok.Claims.FingerprintHash = _fingerprint;
        tok.Signature = ActivationCrypto.Sign(tok.Claims, _fingerprint, _options.ProductId);
        tok.Issuer = "mugobyte-account-free";
        stored!.LastCloudOkUtc = DateTimeOffset.UtcNow;
        _identity.SaveActivation(stored);
        CurrentActivation = stored;
        _log.Info("license", "activation migrated to free tier");
        return true;
    }

    private ActivationResult PersistToken(ActivationResult result)
    {
        if (result.Token is null) return result;
        var matchedFp = DeviceFingerprint.VerifyWithCompatibleFingerprints(result.Token, _options.ProductId);
        if (matchedFp is null)
        {
            _log.Error("license", "refusing unsigned or mismatched activation token");
            return new ActivationResult { Ok = false, Message = "Activation token signature invalid." };
        }

        if (!DeviceFingerprint.Matches(result.Token.Claims.FingerprintHash))
            return new ActivationResult { Ok = false, Message = "Activation is bound to a different device." };

        var stored = new StoredActivation
        {
            Token = result.Token,
            LastCloudOkUtc = DateTimeOffset.UtcNow,
            StoredAtUtc = DateTimeOffset.UtcNow
        };
        _identity.SaveActivation(stored);
        CurrentActivation = stored;
        _log.Info("license", $"activation stored plan={result.Token.Claims.LicenseType}");
        _guard.Evaluate();
        return result;
    }
}

public sealed class LicenseGuard : ILicenseGuard
{
    private readonly PlatformOptions _options;
    private readonly IdentityStore _identity;
    private readonly IPlatformLog _log;
    private LicenseStatus _last = new() { State = LicenseState.Unactivated, Message = "Not activated" };

    public LicenseGuard(PlatformOptions options, IdentityStore identity, IPlatformLog log)
    {
        _options = options;
        _identity = identity;
        _log = log;
    }

    public event Action<LicenseStatus>? StateChanged;

    public bool HasFeature(string featureId)
    {
        var status = Evaluate();
        if (status.Claims?.Features is null) return false;
        return status.Claims.Features.TryGetValue(featureId, out var on) && on;
    }

    public LicenseStatus Evaluate()
    {
        var stored = _identity.LoadActivation();
        if (stored?.Token is null)
        {
            return Publish(new LicenseStatus
            {
                State = LicenseState.Unactivated,
                Message = "Activate Pulse with your MugoByte account.",
                AllowsCoreUse = false,
                RequiresReconnect = true
            });
        }

        var matchedFp = DeviceFingerprint.VerifyWithCompatibleFingerprints(stored.Token, _options.ProductId);
        if (matchedFp is null)
        {
            _log.Error("license", "tamper or signature failure");
            return Publish(new LicenseStatus
            {
                State = LicenseState.Tampered,
                Message = "Activation data is corrupted or was modified. Please sign in and reactivate.",
                AllowsCoreUse = false,
                RequiresReconnect = true
            });
        }

        var claims = stored.Token.Claims;

        // Anti-rollback (MBT POS pattern): a last-cloud timestamp in the future means
        // clock tampering to extend grace. Clamp instead of trusting it.
        if (stored.LastCloudOkUtc > DateTimeOffset.UtcNow.AddHours(1))
        {
            _log.Warn("license", "clock rollback suspected — clamping LastCloudOkUtc");
            stored.LastCloudOkUtc = DateTimeOffset.UtcNow;
            _identity.SaveActivation(stored);
        }

        // Server-authoritative block: applies once recorded (i.e., we reached the portal
        // and it answered). A cached verdict newer than the last cloud-ok wins over grace.
        var verdict = _identity.LoadServerVerdict();
        if (verdict is { Blocked: true } && verdict.Utc >= stored.LastCloudOkUtc)
        {
            return Publish(new LicenseStatus
            {
                State = LicenseState.Blocked,
                Message = string.IsNullOrWhiteSpace(verdict.Message)
                    ? "This device has been blocked from Pulse in the MugoByte Portal."
                    : verdict.Message,
                Claims = claims,
                LastCloudOkUtc = stored.LastCloudOkUtc,
                AllowsCoreUse = false,
                RequiresReconnect = true
            });
        }

        if (!DeviceFingerprint.Matches(claims.FingerprintHash))
        {
            return Publish(new LicenseStatus
            {
                State = LicenseState.DeviceMismatch,
                Message = "This activation belongs to another device.",
                Claims = claims,
                AllowsCoreUse = false,
                RequiresReconnect = true
            });
        }

        if (claims.ExpiresAt is DateTimeOffset exp && exp < DateTimeOffset.UtcNow)
        {
            return Publish(new LicenseStatus
            {
                State = LicenseState.Expired,
                Message = "Your subscription has expired. Renew in the MugoByte Portal.",
                Claims = claims,
                LastCloudOkUtc = stored.LastCloudOkUtc,
                AllowsCoreUse = false,
                RequiresReconnect = true
            });
        }

        // Soft-lock after offline grace: keep local token, require online validate (silent if possible).
        // Free tier is exempt — an account-licensed product never hard-locks.
        var isFreeTier = string.Equals(claims.LicenseType, "free", StringComparison.OrdinalIgnoreCase);
        var graceDays = Math.Max(1, claims.OfflineGraceDays > 0 ? claims.OfflineGraceDays : _options.OfflineGraceDays);
        var elapsed = DateTimeOffset.UtcNow - stored.LastCloudOkUtc;
        var offlineDays = (int)elapsed.TotalDays;
        var remaining = graceDays - offlineDays;

        if (!isFreeTier && elapsed.TotalDays > graceDays)
        {
            // POS offline_lock applies ONLY when the machine is truly offline. If the OS
            // reports a network, degrade to a warning so an unreachable / erroring portal
            // can never hard-lock a connected user; PlatformSyncHost keeps retrying.
            if (!NetworkState.IsAvailable())
            {
                return Publish(new LicenseStatus
                {
                    State = LicenseState.GraceExpired,
                    Message =
                        $"Must connect to internet — offline for {offlineDays} days (limit {graceDays}). " +
                        "Activation is retained; reconnect to unlock.",
                    Claims = claims,
                    LastCloudOkUtc = stored.LastCloudOkUtc,
                    GraceDaysRemaining = 0,
                    IsOffline = true,
                    RequiresReconnect = true,
                    AllowsCoreUse = false
                });
            }

            return Publish(new LicenseStatus
            {
                State = LicenseState.GraceWarning,
                Message =
                    $"Reconnecting to MugoByte Portal… (last validated {offlineDays} day(s) ago, limit {graceDays}).",
                Claims = claims,
                LastCloudOkUtc = stored.LastCloudOkUtc,
                GraceDaysRemaining = 0,
                IsOffline = false,
                RequiresReconnect = true,
                AllowsCoreUse = true
            });
        }

        // POS: critical ≤3d, warning ≤7d remaining grace / subscription.
        var state = remaining <= 3 ? LicenseState.GraceWarning
            : claims.ExpiresAt is DateTimeOffset e2 && (e2 - DateTimeOffset.UtcNow).TotalDays <= 7
                ? LicenseState.Expiring
                : LicenseState.Active;

        return Publish(new LicenseStatus
        {
            State = state,
            Message = state == LicenseState.GraceWarning
                ? $"Critical — reconnect soon ({Math.Max(0, remaining)} day(s) of offline grace left, limit {graceDays})."
                : state == LicenseState.Expiring
                    ? $"Subscription expiring soon · {claims.PlanDisplayName}"
                    : $"{claims.PlanDisplayName} · {claims.LicenseType}",
            Claims = claims,
            LastCloudOkUtc = stored.LastCloudOkUtc,
            GraceDaysRemaining = Math.Max(0, remaining),
            AllowsCoreUse = true
        });
    }

    private LicenseStatus Publish(LicenseStatus status)
    {
        if (_last.State != status.State || _last.Message != status.Message)
        {
            _last = status;
            try { StateChanged?.Invoke(status); } catch { }
        }
        else
        {
            _last = status;
        }
        return status;
    }
}

public sealed class PlatformSyncHost : IPlatformSync, IDisposable
{
    private readonly PlatformOptions _options;
    private readonly IActivationService _activation;
    private readonly IPortalUpdateClient _updates;
    private readonly IUpdateFallback _updateFallback;
    private readonly ILicenseGuard _guard;
    private readonly IPlatformLog _log;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _networkSyncQueued;
    private DateTimeOffset _lastNetworkSync = DateTimeOffset.MinValue;

    public PlatformSyncHost(
        PlatformOptions options,
        IActivationService activation,
        IPortalUpdateClient updates,
        IUpdateFallback updateFallback,
        ILicenseGuard guard,
        IPlatformLog log)
    {
        _options = options;
        _activation = activation;
        _updates = updates;
        _updateFallback = updateFallback;
        _guard = guard;
        _log = log;
    }

    public UpdateCheckResult? LastUpdate { get; private set; }
    public IReadOnlyList<string> Announcements { get; private set; } = [];

    /// <summary>Fired on any thread when sync discovers an available update (Portal or GitHub fallback).</summary>
    public event Action<UpdateCheckResult>? UpdateDiscovered;

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Dispose()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
    }

    public async Task<SyncSnapshot> SynchronizeAsync(CancellationToken ct = default)
    {
        var status = _guard.Evaluate();
        UpdateCheckResult? update = null;
        try
        {
            update = await UpdateResolver.ResolveAsync(
                _updates, _updateFallback, _activation, _options.AppVersion, _log, ct);

            LastUpdate = update;
            if (update is { UpdateAvailable: true }
                && !string.IsNullOrWhiteSpace(update.LatestVersion)
                && !string.IsNullOrWhiteSpace(update.DownloadUrl))
            {
                try { UpdateDiscovered?.Invoke(update); }
                catch (Exception ex) { _log.Warn("update", "UpdateDiscovered handler: " + ex.Message); }
            }
        }
        catch (Exception ex)
        {
            _log.Warn("sync", "update check: " + ex.Message);
        }

        if (_activation.IsActivated)
        {
            try
            {
                await _activation.TrySilentReconnectAsync(ct);
            }
            catch (Exception ex)
            {
                _log.Warn("sync", "license: " + ex.Message);
            }
        }

        status = _guard.Evaluate();
        var flags = status.Claims?.Features ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        return new SyncSnapshot
        {
            SyncedAtUtc = DateTimeOffset.UtcNow,
            License = status,
            Announcements = Announcements,
            FeatureFlags = new Dictionary<string, bool>(flags, StringComparer.OrdinalIgnoreCase),
            Update = update
        };
    }

    private void OnNetworkChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable)
            QueueNetworkSync("availability");
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        if (NetworkState.IsAvailable())
            QueueNetworkSync("address");
    }

    private void QueueNetworkSync(string reason)
    {
        if (Interlocked.Exchange(ref _networkSyncQueued, 1) == 1)
            return;

        var ct = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                if (DateTimeOffset.UtcNow - _lastNetworkSync < TimeSpan.FromSeconds(8))
                    return;
                if (!NetworkState.IsAvailable())
                    return;

                _log.Info("sync", "network restored (" + reason + ") — silent license refresh");
                _lastNetworkSync = DateTimeOffset.UtcNow;
                await _activation.TrySilentReconnectAsync(ct);
                await SynchronizeAsync(ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.Warn("sync", "network sync: " + ex.Message); }
            finally { Interlocked.Exchange(ref _networkSyncQueued, 0); }
        }, ct);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(4), ct); } catch { return; }

        var lastCloud = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _guard.Evaluate();

                var now = DateTimeOffset.UtcNow;
                var due = now - lastCloud >= _options.SyncInterval;
                var grace = _guard.Evaluate();
                var needNow = grace.State is LicenseState.GraceExpired or LicenseState.GraceWarning
                              && NetworkState.IsAvailable();

                if (due || needNow)
                {
                    await SynchronizeAsync(ct);
                    lastCloud = now;
                }
            }
            catch (Exception ex) { _log.Warn("sync", ex.Message); }

            try { await Task.Delay(_options.LocalValidateInterval, ct); }
            catch { break; }
        }
    }
}
