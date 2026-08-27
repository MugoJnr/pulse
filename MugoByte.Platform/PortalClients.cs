using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace MugoByte.Platform;

/// <summary>HTTP client mirroring MBT POS portal contracts (portal.mugobyte.com).</summary>
public sealed class PortalAuthClient : IPortalAuthClient
{
    private readonly HttpClient _http;
    private readonly IPlatformLog _log;

    public PortalAuthClient(HttpClient http, IPlatformLog log)
    {
        _http = http;
        _log = log;
    }

    public async Task<AuthResult> SignInAsync(string email, string password, CancellationToken ct = default)
    {
        try
        {
            using var res = await _http.PostAsJsonAsync("/api/cloud/auth/login", new { email, password }, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                return new AuthResult { Ok = false, Message = ExtractMessage(body) ?? $"Sign-in failed ({(int)res.StatusCode})" };

            var session = ParseSession(body);
            if (session is null)
                return new AuthResult { Ok = false, Message = "Invalid sign-in response from portal." };

            _log.Info("auth", "sign-in ok");
            return new AuthResult { Ok = true, Message = "Signed in", Session = session };
        }
        catch (Exception ex)
        {
            _log.Warn("auth", "sign-in error: " + ex.Message);
            return new AuthResult { Ok = false, Message = FriendlyNetwork(ex) };
        }
    }

    public async Task<AuthResult> SignUpAsync(string email, string password, string? fullName = null, CancellationToken ct = default)
    {
        try
        {
            using var res = await _http.PostAsJsonAsync("/api/cloud/auth/register", new
            {
                email,
                password,
                full_name = fullName
            }, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = doc.RootElement;
            var verification = root.TryGetProperty("verification_required", out var v) && v.GetBoolean();
            if (!res.IsSuccessStatusCode)
                return new AuthResult { Ok = false, Message = ExtractMessage(body) ?? "Registration failed." };

            _log.Info("auth", "register ok");
            return new AuthResult
            {
                Ok = true,
                VerificationRequired = verification,
                Message = ExtractMessage(body) ?? (verification
                    ? "Check your email to verify the account."
                    : "Account created.")
            };
        }
        catch (Exception ex)
        {
            _log.Warn("auth", "register error: " + ex.Message);
            return new AuthResult { Ok = false, Message = FriendlyNetwork(ex) };
        }
    }

    public async Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/cloud/auth/session");
            req.Content = JsonContent.Create(new { refresh_token = refreshToken });
            using var res = await _http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                return new AuthResult { Ok = false, Message = ExtractMessage(body) ?? "Session refresh failed." };
            var session = ParseSession(body);
            return session is null
                ? new AuthResult { Ok = false, Message = "Invalid refresh response." }
                : new AuthResult { Ok = true, Session = session, Message = "refreshed" };
        }
        catch (Exception ex)
        {
            return new AuthResult { Ok = false, Message = FriendlyNetwork(ex) };
        }
    }

    public async Task<MugoByteUser?> GetProfileAsync(string accessToken, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/cloud/auth/me");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            var body = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var userEl = root.TryGetProperty("user", out var u) ? u : root;
            return new MugoByteUser
            {
                Id = Prop(userEl, "id"),
                Email = Prop(userEl, "email"),
                DisplayName = Prop(userEl, "full_name", "name", "display_name") is { Length: > 0 } n ? n : Prop(userEl, "email"),
                AvatarUrl = NullIfEmpty(Prop(userEl, "avatar_url", "picture")),
                Role = NullIfEmpty(Prop(userEl, "role", "platform_role"))
            };
        }
        catch
        {
            return null;
        }
    }

    public Task SignOutAsync(CancellationToken ct = default)
    {
        _log.Info("auth", "sign-out");
        return Task.CompletedTask;
    }

    private static AuthSession? ParseSession(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            // Some portal builds nest tokens under session / data.
            var bag = root;
            if (root.TryGetProperty("session", out var nested) && nested.ValueKind == JsonValueKind.Object)
                bag = nested;
            else if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                bag = data;

            var token = Prop(bag, "token", "access_token", "accessToken");
            if (string.IsNullOrWhiteSpace(token))
                token = Prop(root, "token", "access_token", "accessToken");
            if (string.IsNullOrWhiteSpace(token)) return null;

            var refresh = Prop(bag, "refresh_token", "refreshToken");
            if (string.IsNullOrWhiteSpace(refresh))
                refresh = Prop(root, "refresh_token", "refreshToken");

            MugoByteUser user = new();
            var userEl = root.TryGetProperty("user", out var u) ? u
                : bag.TryGetProperty("user", out var u2) ? u2
                : default;
            if (userEl.ValueKind == JsonValueKind.Object)
            {
                user = new MugoByteUser
                {
                    Id = Prop(userEl, "id"),
                    Email = Prop(userEl, "email"),
                    DisplayName = Prop(userEl, "full_name", "name") is { Length: > 0 } n ? n : Prop(userEl, "email"),
                    Role = NullIfEmpty(Prop(userEl, "role"))
                };
            }

            DateTimeOffset? expiresAt = null;
            if (TryExpires(bag, out var expBag) || TryExpires(root, out expBag))
                expiresAt = expBag;
            // Expiry hint from JWT payload when portal omits expires_at (no signature verify).
            if (expiresAt is null && AccessTokenExpiry.TryReadExp(token, out var jwtExp))
                expiresAt = jwtExp;

            return new AuthSession
            {
                AccessToken = token,
                RefreshToken = refresh,
                ExpiresAt = expiresAt,
                Provider = Prop(root, "provider") is { Length: > 0 } p ? p
                    : Prop(bag, "provider") is { Length: > 0 } p2 ? p2 : "supabase",
                User = user
            };
        }
        catch { return null; }
    }

    private static bool TryExpires(JsonElement el, out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        if (el.ValueKind != JsonValueKind.Object) return false;

        foreach (var name in new[] { "expires_at", "expiresAt" })
        {
            if (!el.TryGetProperty(name, out var ea)) continue;
            if (ea.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(ea.GetString(), out var parsedExp))
            {
                expiresAt = parsedExp;
                return true;
            }
            if (ea.ValueKind == JsonValueKind.Number)
            {
                // Unix seconds (or ms if clearly large).
                if (ea.TryGetInt64(out var n))
                {
                    expiresAt = n > 10_000_000_000L
                        ? DateTimeOffset.FromUnixTimeMilliseconds(n)
                        : DateTimeOffset.FromUnixTimeSeconds(n);
                    return true;
                }
                if (ea.TryGetDouble(out var d) && d > 0)
                {
                    var asLong = (long)d;
                    expiresAt = asLong > 10_000_000_000L
                        ? DateTimeOffset.FromUnixTimeMilliseconds(asLong)
                        : DateTimeOffset.FromUnixTimeSeconds(asLong);
                    return true;
                }
            }
        }

        foreach (var name in new[] { "expires_in", "expiresIn" })
        {
            if (!el.TryGetProperty(name, out var ei)) continue;
            var seconds = ei.ValueKind == JsonValueKind.Number ? ei.GetInt32() : 0;
            if (seconds > 0)
            {
                expiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
                return true;
            }
        }
        return false;
    }

    private static string Prop(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? "";
        }
        return "";
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string? ExtractMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return Prop(doc.RootElement, "message", "error", "detail");
        }
        catch { return null; }
    }

    private static string FriendlyNetwork(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException
            ? "Portal unavailable. Check your Internet connection or try demo mode."
            : ex.Message;
}

public sealed class PortalLicenseClient : IPortalLicenseClient
{
    private readonly HttpClient _http;
    private readonly PlatformOptions _options;
    private readonly IPlatformLog _log;

    public PortalLicenseClient(HttpClient http, PlatformOptions options, IPlatformLog log)
    {
        _http = http;
        _options = options;
        _log = log;
    }

    public Task<ActivationResult> ClaimAsync(string accessToken, string deviceId, string fingerprintHash, CancellationToken ct = default) =>
        ActivateInternalAsync(accessToken, deviceId, fingerprintHash, null, claim: true, ct);

    public Task<ActivationResult> ActivateAsync(string accessToken, string deviceId, string fingerprintHash, string? licenseKey = null, CancellationToken ct = default) =>
        ActivateInternalAsync(accessToken, deviceId, fingerprintHash, licenseKey, claim: false, ct);

    private async Task<ActivationResult> ActivateInternalAsync(
        string accessToken, string deviceId, string fingerprintHash, string? licenseKey, bool claim, CancellationToken ct)
    {
        try
        {
            var path = claim ? "/api/cloud/licenses/claim" : "/api/cloud/licenses/activate";
            using var req = new HttpRequestMessage(HttpMethod.Post, path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            req.Content = JsonContent.Create(new
            {
                device_id = deviceId,
                hardware_fingerprint = fingerprintHash,
                product_id = _options.ProductId,
                app_version = _options.AppVersion,
                license_key = licenseKey,
                key = licenseKey
            });
            using var res = await _http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                _log.Warn("license", $"activate failed {(int)res.StatusCode}");
                var detail = Extract(body) ?? "Activation failed.";
                if (detail.Contains("not reserved", StringComparison.OrdinalIgnoreCase)
                    || detail.Contains("No pulse", StringComparison.OrdinalIgnoreCase)
                    || detail.Contains("product", StringComparison.OrdinalIgnoreCase))
                {
                    detail += " Open portal.mugobyte.com → Admin → Licenses and issue a Pulse seat, or paste a Pulse license key.";
                }
                return new ActivationResult { Ok = false, Message = detail };
            }

            var token = BuildTokenFromResponse(body, deviceId, fingerprintHash);
            if (token is null)
                return new ActivationResult { Ok = false, Message = "Portal returned an incomplete activation payload." };

            _log.Info("license", "activated");
            return new ActivationResult
            {
                Ok = true,
                Message = $"Cloud license activated! Plan: {token.Claims.PlanDisplayName}",
                Token = token,
                Plan = new LicensePlanInfo
                {
                    PlanId = token.Claims.PlanId,
                    DisplayName = token.Claims.PlanDisplayName,
                    LicenseType = token.Claims.LicenseType,
                    MaxDevices = token.Claims.MaxDevices,
                    ExpiresAt = token.Claims.ExpiresAt,
                    Features = token.Claims.Features
                }
            };
        }
        catch (Exception ex)
        {
            _log.Warn("license", ex.Message);
            return new ActivationResult { Ok = false, Message = Friendly(ex) };
        }
    }

    public async Task<LicenseStatus> ValidateAsync(string accessToken, string deviceId, string fingerprintHash, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/cloud/licenses/validate");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            req.Content = JsonContent.Create(new
            {
                device_id = deviceId,
                hardware_fingerprint = fingerprintHash,
                product_id = _options.ProductId
            });
            using var res = await _http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                var code = (int)res.StatusCode;
                var unauthorized = code is 401 or 403;
                var detail = Extract(body) ?? "Could not validate with portal.";

                // Server-authoritative revocation/blocking: a confirmed 403 with
                // revoked/blocked/disabled wording must never be softened to a warning.
                if (code == 403 && IsRevocationWording(detail))
                {
                    return new LicenseStatus
                    {
                        State = LicenseState.Blocked,
                        Message = $"MugoByte Portal has blocked this device from Pulse. ({detail})",
                        IsOffline = false,
                        AllowsCoreUse = false,
                        RequiresReconnect = true
                    };
                }

                var endpointMissing = code is 404 or 405 or 501;
                if (endpointMissing)
                    detail = $"Portal license API not deployed (HTTP {code}). {detail}";
                return new LicenseStatus
                {
                    State = LicenseState.GraceWarning,
                    Message = detail,
                    IsOffline = false,
                    AllowsCoreUse = true,
                    NeedsAuthRefresh = unauthorized,
                    EndpointMissing = endpointMissing
                };
            }

            return new LicenseStatus
            {
                State = LicenseState.Active,
                Message = "Validated with portal",
                LastCloudOkUtc = DateTimeOffset.UtcNow,
                AllowsCoreUse = true
            };
        }
        catch
        {
            return new LicenseStatus
            {
                State = LicenseState.GraceWarning,
                Message = "Offline — using local activation.",
                IsOffline = true,
                AllowsCoreUse = true
            };
        }
    }

    private static bool IsRevocationWording(string detail) =>
        detail.Contains("revoked", StringComparison.OrdinalIgnoreCase)
        || detail.Contains("blocked", StringComparison.OrdinalIgnoreCase)
        || detail.Contains("banned", StringComparison.OrdinalIgnoreCase)
        || detail.Contains("disabled", StringComparison.OrdinalIgnoreCase)
        || detail.Contains("suspended", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<DeviceDescriptor>> ListDevicesAsync(string accessToken, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/cloud/devices?product_id=" + Uri.EscapeDataString(_options.ProductId));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return [];
            var body = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var list = new List<DeviceDescriptor>();
            var root = doc.RootElement;
            var arr = root.ValueKind == JsonValueKind.Array ? root
                : root.TryGetProperty("devices", out var d) ? d : default;
            if (arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in arr.EnumerateArray())
            {
                list.Add(new DeviceDescriptor
                {
                    DeviceId = Get(item, "device_id", "id"),
                    DisplayName = Get(item, "name", "display_name", "device_name"),
                    OsVersion = Get(item, "os_version", "os"),
                    AppVersion = Get(item, "app_version", "mbt_version"),
                    FingerprintHash = "",
                    LastOnlineAt = null
                });
            }
            return list;
        }
        catch { return []; }
    }

    public async Task<PlatformResult> DeactivateDeviceAsync(string accessToken, string deviceId, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/cloud/licenses/deactivate");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            req.Content = JsonContent.Create(new { device_id = deviceId, product_id = _options.ProductId });
            using var res = await _http.SendAsync(req, ct);
            return res.IsSuccessStatusCode
                ? PlatformResult.Success("Device deactivated")
                : PlatformResult.Fail("Deactivation failed");
        }
        catch (Exception ex)
        {
            return PlatformResult.Fail(ex.Message);
        }
    }

    private ActivationToken? BuildTokenFromResponse(string body, string deviceId, string fingerprintHash)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var act = root.TryGetProperty("activation", out var a) ? a : root;
            var lic = root.TryGetProperty("license", out var l) ? l : root;
            var plan = Get(lic, "plan", "plan_id", "license_type");
            if (string.IsNullOrWhiteSpace(plan)) plan = "standard";

            var claims = new ActivationClaims
            {
                UserId = Get(root, "user_id") is { Length: > 0 } uid ? uid : Get(act, "user_id"),
                UserEmail = Get(root, "email"),
                ProductId = _options.ProductId,
                LicenseType = NormalizePlan(plan),
                PlanId = plan.ToLowerInvariant(),
                PlanDisplayName = plan,
                DeviceId = deviceId,
                FingerprintHash = fingerprintHash,
                MaxDevices = TryInt(lic, "max_devices") ?? 1,
                OfflineGraceDays = TryInt(lic, "offline_grace_days")
                    ?? TryInt(act, "offline_grace_days")
                    ?? _options.OfflineGraceDays,
                MinAppVersion = "1.0.0",
                // Portal is source of truth — never invent client feature maps.
                Features = ParseFeatures(lic) ?? ParseFeatures(act) ?? ParseFeatures(root)
                    ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            };

            if (DateTimeOffset.TryParse(Get(lic, "expires_at") is { Length: > 0 } e ? e : Get(act, "expires_at"), out var exp))
                claims.ExpiresAt = exp;

            // Prefer portal signature when present; otherwise sign device-bound envelope.
            var portalSig = Get(act, "signature", "token_signature");
            var token = new ActivationToken
            {
                Claims = claims,
                Issuer = string.IsNullOrWhiteSpace(portalSig) ? "mugobyte-local-bound" : "mugobyte-portal",
                IssuedAt = DateTimeOffset.UtcNow,
                Signature = string.IsNullOrWhiteSpace(portalSig)
                    ? ActivationCrypto.Sign(claims, fingerprintHash, _options.ProductId)
                    : portalSig
            };

            // If portal signature is opaque and not our HMAC, re-bind with local HMAC of claims
            // so offline verification always has a checkable signature. Portal signature retained in Issuer.
            if (!ActivationCrypto.Verify(token, fingerprintHash, _options.ProductId))
            {
                token.Signature = ActivationCrypto.Sign(claims, fingerprintHash, _options.ProductId);
                token.Issuer = "mugobyte-portal+local-bind";
            }

            return token;
        }
        catch { return null; }
    }

    private static Dictionary<string, bool>? ParseFeatures(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty("features", out var feats)) return null;

        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (feats.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in feats.EnumerateObject())
            {
                map[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => prop.Value.TryGetInt32(out var n) && n != 0,
                    JsonValueKind.String => bool.TryParse(prop.Value.GetString(), out var b) && b,
                    _ => false
                };
            }
            return map;
        }

        if (feats.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in feats.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } name)
                    map[name] = true;
            }
            return map;
        }

        return null;
    }

    private static string NormalizePlan(string plan)
    {
        plan = plan.ToLowerInvariant();
        if (plan.Contains("trial")) return "trial";
        if (plan.Contains("life")) return "lifetime";
        if (plan.Contains("year") || plan.Contains("annual")) return "yearly";
        if (plan.Contains("month")) return "monthly";
        if (plan.Contains("enterprise")) return "enterprise";
        if (plan.Contains("business")) return "business";
        return plan;
    }

    private static string Get(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(n, out var v))
            {
                if (v.ValueKind == JsonValueKind.String) return v.GetString() ?? "";
                if (v.ValueKind == JsonValueKind.Number) return v.ToString();
            }
        }
        return "";
    }

    private static int? TryInt(JsonElement el, string name)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.TryGetInt32(out var i))
            return i;
        return null;
    }

    private static string? Extract(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var n in new[] { "message", "error", "detail" })
                if (doc.RootElement.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
        }
        catch { }
        return null;
    }

    private static string Friendly(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException
            ? "Portal unavailable during activation."
            : ex.Message;
}

public sealed class PortalUpdateClient : IPortalUpdateClient
{
    private readonly HttpClient _http;
    private readonly PlatformOptions _options;
    private readonly IdentityStore _identity;
    private readonly IPlatformLog _log;
    private static DateTimeOffset _lastAuthLogUtc = DateTimeOffset.MinValue;

    public PortalUpdateClient(HttpClient http, PlatformOptions options, IdentityStore identity, IPlatformLog log)
    {
        _http = http;
        _options = options;
        _identity = identity;
        _log = log;
    }

    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        try
        {
            var url = $"/api/cloud/updates?product_id={Uri.EscapeDataString(_options.ProductId)}&current_version={Uri.EscapeDataString(currentVersion)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            var session = _identity.LoadSession();
            if (!string.IsNullOrWhiteSpace(session?.AccessToken))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

            using var res = await _http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);

            if (res.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                // Soft failure: caller refreshes session and retries. Never fatal / never kill the app.
                var hasToken = session?.AccessToken is { Length: > 0 };
                if (!hasToken && DateTimeOffset.UtcNow - _lastAuthLogUtc > TimeSpan.FromHours(6))
                {
                    _lastAuthLogUtc = DateTimeOffset.UtcNow;
                    _log.Info("update", "update check skipped — sign in required for portal update feed");
                }

                return new UpdateCheckResult
                {
                    NeedsAuthRefresh = hasToken,
                    Message = hasToken
                        ? "Update check needs a refreshed sign-in."
                        : "Sign in to check for Pulse updates."
                };
            }

            if (!res.IsSuccessStatusCode)
            {
                if (DateTimeOffset.UtcNow - _lastAuthLogUtc > TimeSpan.FromHours(1))
                {
                    _lastAuthLogUtc = DateTimeOffset.UtcNow;
                    _log.Warn("update", $"check {(int)res.StatusCode}");
                }
                return new UpdateCheckResult { Message = "Update service unavailable." };
            }

            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = doc.RootElement;

            // Portal returns nested latest / latest_for_client objects (MBT POS shape).
            var tip = root.TryGetProperty("latest_for_client", out var lfc) && lfc.ValueKind == JsonValueKind.Object
                ? lfc
                : root.TryGetProperty("latest", out var lat) && lat.ValueKind == JsonValueKind.Object
                    ? lat
                    : root;

            var latest = Get(tip, "latest_version", "version") is { Length: > 0 } v
                ? v
                : Get(root, "latest_version", "version");

            var available = (root.TryGetProperty("update_available", out var ua) && ua.ValueKind == JsonValueKind.True)
                            || (!string.IsNullOrWhiteSpace(latest)
                                && !string.Equals(latest, currentVersion, StringComparison.OrdinalIgnoreCase)
                                && IsNewerVersion(latest, currentVersion));

            // Prefer Pulse rows when product_id is present in the list.
            if (root.TryGetProperty("updates", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in arr.EnumerateArray())
                {
                    var pid = Get(row, "product_id", "app_id", "product");
                    if (!string.IsNullOrWhiteSpace(pid) &&
                        !string.Equals(pid, _options.ProductId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var rowVer = Get(row, "version", "latest_version");
                    if (string.IsNullOrWhiteSpace(rowVer)) continue;
                    if (IsNewerVersion(rowVer, currentVersion))
                    {
                        available = true;
                        latest = rowVer;
                        tip = row;
                        break;
                    }
                }
            }

            return new UpdateCheckResult
            {
                UpdateAvailable = available,
                LatestVersion = latest,
                DownloadUrl = Get(tip, "download_url") is { Length: > 0 } d ? d : Get(root, "download_url"),
                ChecksumSha256 = Get(tip, "checksum_sha256") is { Length: > 0 } c ? c : Get(root, "checksum_sha256"),
                ReleaseNotes = Get(tip, "release_notes") is { Length: > 0 } r ? r : Get(root, "release_notes"),
                IsMandatory = (tip.TryGetProperty("is_mandatory", out var m) && m.ValueKind == JsonValueKind.True)
                              || (root.TryGetProperty("is_mandatory", out var m2) && m2.ValueKind == JsonValueKind.True),
                Message = available
                    ? $"Update {latest} available"
                    : string.IsNullOrWhiteSpace(latest)
                        ? "No Pulse update published yet."
                        : "You are up to date."
            };
        }
        catch (Exception ex)
        {
            if (DateTimeOffset.UtcNow - _lastAuthLogUtc > TimeSpan.FromHours(1))
            {
                _lastAuthLogUtc = DateTimeOffset.UtcNow;
                _log.Warn("update", ex.Message);
            }
            return new UpdateCheckResult { Message = "Could not reach update service." };
        }
    }

    private static bool IsNewerVersion(string candidate, string current)
    {
        try
        {
            var a = NormalizeVersion(candidate);
            var b = NormalizeVersion(current);
            return a > b;
        }
        catch
        {
            return !string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static Version NormalizeVersion(string raw)
    {
        var cleaned = new string(raw.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
        if (string.IsNullOrWhiteSpace(cleaned)) return new Version(0, 0, 0, 0);
        if (!cleaned.Contains('.')) cleaned += ".0";
        return Version.Parse(cleaned);
    }

    private static string Get(JsonElement el, params string[] names)
    {
        if (el.ValueKind != JsonValueKind.Object) return "";
        foreach (var n in names)
            if (el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? "";
        return "";
    }
}

public static class PortalHttp
{
    public static HttpClient Create(PlatformOptions options)
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri(options.PortalBaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(8)
        };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"MugoByte-{options.ProductId}/{options.AppVersion}");
        return http;
    }
}
