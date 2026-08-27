# MugoByte Platform Client (shared)

Reusable .NET 8 library for **Pulse**, aligned with **MBT POS** account onboarding, and the intended foundation for **ExamHub** and future MugoByte desktop apps.

## Account onboarding (matches MBT POS)

Pulse follows the same hybrid flow as MBT POS (`licensing/cloud_onboarding.py` + `activation_ui.py`):

1. **Sign In & Activate** with MugoByte Portal credentials  
2. **Silent seat claim** (`POST /api/cloud/licenses/claim`) when a license seat exists  
3. **Manual MBT-… license key** only as fallback (no seat / offline / claim failed)  
4. **Offline grace** defaults to **7 days** (same as POS `OFFLINE_GRACE_DAYS`); override with `MBT_LICENSE_OFFLINE_GRACE_DAYS`  
5. **Soft-lock** after grace: activation stays on disk, app use blocked until online validate (POS `offline_lock`)  
6. **Background ticks**: local revalidate ~5 min, cloud validate ~15 min (`PlatformSyncHost`)

There is **no** production “demo activation” CTA. Local validation uses `--mock-account` / `MBT_PLATFORM_MODE=mock`, which still runs **login → auto-claim**.

## Contracts (from MBT POS portal)

| Area | Portal |
|---|---|
| Auth | `POST /api/cloud/auth/login`, `register`, `session`, `GET …/me` |
| License | `POST /api/cloud/licenses/claim`, `activate`, `validate`, `deactivate` |
| Devices | `GET /api/cloud/devices` |
| Updates | `GET /api/cloud/updates?product_id=&current_version=` |
| Base URL | `https://portal.mugobyte.com` (`MBT_PORTAL_URL`) |

Product id for Pulse: **`pulse`**

## Components

- `IPortalAuthClient` / `PortalAuthClient` / `MockPortalAuthClient`
- `IPortalLicenseClient` / `PortalLicenseClient` / `MockPortalLicenseClient`
- `IPortalUpdateClient` / `PortalUpdateClient` / `MockPortalUpdateClient`
- `DeviceFingerprint` — multi-signal SHA-256 (MachineGuid, BIOS, board, CPU, OS) — **raw IDs never stored**
- `DpapiSecureStore` — Windows DPAPI under `%APPDATA%\MugoByte\{Product}\secure\`
- `ActivationCrypto` — HMAC-bound activation tokens (unsigned local data rejected)
- `IActivationService.SignInAndActivateAsync` — POS-style login → claim
- `LicenseGuard` — offline grace (**7 days** default), soft-lock when grace exceeded
- `PlatformSyncHost` — background license + update sync + device roster touch
- `AddMugoBytePlatform()` — DI registration

Feature flags come from the **portal payload** when present. The client does not invent product feature maps.

## Modes

| Mode | How |
|---|---|
| Live portal | default |
| Mock (same process) | `MBT_PLATFORM_MODE=mock`, `--mock-account`, or settings `UseMockAccount` |
| Skip gate | `--skip-account` (dev only) |

## Integration testing (live portal)

Requires Pulse product registration and license seats on portal.mugobyte.com. Until then, validate with `--mock-account` using **Sign In & Activate**.

## Extracting for other apps

Reference `MugoByte.Platform.csproj` and call:

```csharp
services.AddMugoBytePlatform(PlatformOptions.ForPulse(version, useMock: false));
// OfflineGraceDays defaults to 7 (POS). Override via MBT_LICENSE_OFFLINE_GRACE_DAYS or options.
```
