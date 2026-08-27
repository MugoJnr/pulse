using CpuTempWidget.Services;
using MugoByte.Platform;
using Xunit;

namespace Pulse.Tests;

public class DeviceFingerprintTests
{
    [Fact]
    public void ComputeHash_IsStableAcrossCalls()
    {
        var a = DeviceFingerprint.ComputeHash();
        var b = DeviceFingerprint.ComputeHash();
        Assert.False(string.IsNullOrWhiteSpace(a));
        Assert.Equal(a, b);
    }

    [Fact]
    public void Matches_AcceptsV1OrV2()
    {
        var v2 = DeviceFingerprint.ComputeHash();
        var v1 = DeviceFingerprint.ComputeHashLegacyV1();
        Assert.True(DeviceFingerprint.Matches(v2));
        Assert.True(DeviceFingerprint.Matches(v1));
        Assert.False(DeviceFingerprint.Matches("not-a-real-fingerprint"));
    }
}

public class SystemMonitorTests
{
    [Fact]
    public void Read_ReturnsFiniteCpuAndRam_AndSaneTemp()
    {
        using var mon = new SystemMonitor();
        var r = mon.Read();
        Assert.True(float.IsFinite(r.CpuPercent));
        Assert.True(float.IsFinite(r.RamPercent));
        Assert.InRange(r.RamPercent, 0f, 100f);
        if (r.TemperatureC is float t)
            Assert.InRange(t, 10f, 125f);
    }

    [Fact]
    public async Task TemperatureSource_NonEmptyAfterSamplerWait()
    {
        SystemMonitor.EnsureSampler();
        await Task.Delay(2200);
        using var mon = new SystemMonitor();
        _ = mon.Read();
        await Task.Delay(500);
        var src = SystemMonitor.LastTemperatureSource;
        Assert.False(string.IsNullOrWhiteSpace(src));
    }
}

public class PowerResilienceTests
{
    [Fact]
    public void SimulateTransition_x20_DoesNotThrow()
    {
        for (var i = 0; i < 20; i++)
            PowerResilienceService.SimulateTransition(i % 2 == 0 ? "Battery" : "AC");
    }

    [Fact]
    public void NotifyPowerTransition_x20_Ok()
    {
        for (var i = 0; i < 20; i++)
            SystemMonitor.NotifyPowerTransition($"test-{i}");
    }

    [Fact]
    public void ChargerStress_AlternatingBatteryAc_PlusNotify()
    {
        for (var i = 0; i < 20; i++)
            PowerResilienceService.SimulateTransition(i % 2 == 0 ? "Battery" : "AC");
        for (var i = 0; i < 20; i++)
            SystemMonitor.NotifyPowerTransition($"charger-stress-{i}");
    }
}

public class BatteryChargeMeterTests
{
    [Fact]
    public void ReadChargeWatts_DoesNotThrow()
    {
        var _ = BatteryChargeMeter.ReadChargeWatts(true);
        var __ = BatteryChargeMeter.ReadChargeWatts(false);
    }
}

public class ActivationCryptoTests
{
    [Fact]
    public void SignVerify_Roundtrip_WithComputeHash()
    {
        var fp = DeviceFingerprint.ComputeHash();
        var productId = PlatformOptions.PulseProductId;
        var claims = new ActivationClaims
        {
            UserId = "test-user",
            UserEmail = "test@example.com",
            ProductId = productId,
            FingerprintHash = fp,
            PlanId = "free",
            PlanDisplayName = "Pulse Free",
            LicenseType = "free",
            MaxDevices = 1,
            ActivatedAt = DateTimeOffset.UtcNow
        };
        var sig = ActivationCrypto.Sign(claims, fp, productId);
        var token = new ActivationToken
        {
            Claims = claims,
            Signature = sig,
            Issuer = "test"
        };
        Assert.True(ActivationCrypto.Verify(token, fp, productId));
    }
}

public class UpdateServiceTests
{
    [Fact]
    public void VerifyChecksum_RejectsMismatch()
    {
        var bytes = "hello-pulse"u8.ToArray();
        Assert.False(UpdateService.VerifyChecksum(bytes, "deadbeef"));
        Assert.True(UpdateService.VerifyChecksum(bytes, null));
        var good = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.True(UpdateService.VerifyChecksum(bytes, good));
    }
}

public class BootstrapServiceTests
{
    [Fact]
    public void IsInstalledPath_HelpersWork()
    {
        Assert.False(BootstrapService.IsInstalledPath(null));
        Assert.False(BootstrapService.IsInstalledPath("C:\\not\\pulse.exe"));
        Assert.True(BootstrapService.IsInstalledPath(BootstrapService.InstalledExecutable)
                    || !BootstrapService.IsInstalledPath(@"C:\Windows\System32\notepad.exe"));
    }
}

public class AccessTokenExpiryTests
{
    [Fact]
    public void TryReadExp_ParsesJwtPayload()
    {
        // header.payload.sig — payload {"exp":2000000000}
        var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"exp\":2000000000}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var jwt = $"eyJhbGciOiJub25lIn0.{payload}.sig";
        Assert.True(AccessTokenExpiry.TryReadExp(jwt, out var exp));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(2000000000), exp);
    }
}
