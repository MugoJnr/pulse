using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MugoByte.Platform;

public interface ISecureStore
{
    void Save(string name, byte[] plaintext);
    byte[]? Load(string name);
    void Delete(string name);
    bool Exists(string name);
}

/// <summary>DPAPI-backed secure store under %APPDATA%\MugoByte\{product}\secure\.</summary>
public sealed class DpapiSecureStore : ISecureStore
{
    private readonly string _dir;

    public DpapiSecureStore(string productFolder)
    {
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MugoByte", productFolder, "secure");
        Directory.CreateDirectory(_dir);
    }

    public void Save(string name, byte[] plaintext)
    {
        var protectedBytes = ProtectedData.Protect(plaintext, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathFor(name), protectedBytes);
    }

    public byte[]? Load(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return null;
        try
        {
            var raw = File.ReadAllBytes(path);
            return ProtectedData.Unprotect(raw, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
        }
        catch
        {
            return null;
        }
    }

    public void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    public bool Exists(string name) => File.Exists(PathFor(name));

    private string PathFor(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return Path.Combine(_dir, name + ".bin");
    }
}

/// <summary>
/// Multi-signal device fingerprint → SHA-256 hex. Raw identifiers never persisted.
/// v2 is stable across user/OS renames; v1 retained for activation compatibility.
/// </summary>
public static class DeviceFingerprint
{
    /// <summary>Current stable fingerprint (v2). Used for all new activations.</summary>
    public static string ComputeHash()
    {
        var parts = new List<string>
        {
            "v2",
            Safe(ReadMachineGuid),
            Safe(ReadBiosSerial),
            Safe(ReadBoardSerial),
            Safe(ReadCpuId),
            Safe(() => Environment.Is64BitOperatingSystem ? "x64" : "x86")
        };

        var material = string.Join("|", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Pre-1.11 fingerprint including MachineName/UserName/OSVersion.</summary>
    public static string ComputeHashLegacyV1()
    {
        var parts = new List<string>
        {
            "v1",
            Safe(() => Environment.MachineName),
            Safe(() => Environment.UserName),
            Safe(ReadMachineGuid),
            Safe(ReadBiosSerial),
            Safe(ReadBoardSerial),
            Safe(ReadCpuId),
            Safe(() => Environment.OSVersion.VersionString),
            Safe(() => Environment.Is64BitOperatingSystem ? "x64" : "x86")
        };

        var material = string.Join("|", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>True if <paramref name="stored"/> equals v2 or legacy v1.</summary>
    public static bool Matches(string stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return false;
        if (string.Equals(stored, ComputeHash(), StringComparison.OrdinalIgnoreCase))
            return true;
        return string.Equals(stored, ComputeHashLegacyV1(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verify an activation token against v2 or legacy v1 fingerprint.
    /// Returns the fingerprint that verified, or null if neither works.
    /// </summary>
    public static string? VerifyWithCompatibleFingerprints(ActivationToken token, string productId)
    {
        var v2 = ComputeHash();
        if (ActivationCrypto.Verify(token, v2, productId))
            return v2;

        var v1 = ComputeHashLegacyV1();
        if (ActivationCrypto.Verify(token, v1, productId))
            return v1;

        return null;
    }

    /// <summary>
    /// Cloud-facing device id shared across MugoByte products. Prefers the MBT POS
    /// compatible identity at %APPDATA%\MugoByte\.mbt_lic\device.id so POS and Pulse
    /// register the same physical device on the Portal.
    /// </summary>
    public static string GetOrCreateDeviceId(ISecureStore store, string productPrefix = "PULSE")
    {
        var shared = ReadSharedDeviceId();
        if (!string.IsNullOrWhiteSpace(shared)) return shared;

        const string key = "device_id";
        var existing = store.Load(key);
        if (existing is { Length: > 0 })
        {
            var id = Encoding.UTF8.GetString(existing);
            if (!string.IsNullOrWhiteSpace(id))
            {
                WriteSharedDeviceId(id);
                return id;
            }
        }

        // POS-compatible identity: sha256("mg:" + MachineGuid)[:40]
        var mg = Safe(ReadMachineGuid);
        var derived = !string.IsNullOrWhiteSpace(mg)
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("mg:" + mg))).ToLowerInvariant()[..40]
            : Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();
        store.Save(key, Encoding.UTF8.GetBytes(derived));
        WriteSharedDeviceId(derived);
        return derived;
    }

    private static string? ReadSharedDeviceId()
    {
        foreach (var env in new[] { "APPDATA", "LOCALAPPDATA", "ProgramData" })
        {
            var root = Environment.GetEnvironmentVariable(env);
            if (string.IsNullOrWhiteSpace(root)) continue;
            try
            {
                var p = Path.Combine(root, "MugoByte", ".mbt_lic", "device.id");
                if (!File.Exists(p)) continue;
                var did = File.ReadAllText(p).Trim();
                if (did.Length == 40 && did.All(c => Uri.IsHexDigit(c)))
                    return did.ToLowerInvariant();
            }
            catch { }
        }
        return null;
    }

    private static void WriteSharedDeviceId(string deviceId)
    {
        try
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(root, "MugoByte", ".mbt_lic");
            Directory.CreateDirectory(dir);
            var p = Path.Combine(dir, "device.id");
            if (!File.Exists(p)) File.WriteAllText(p, deviceId);
        }
        catch { }
    }

    private static string Safe(Func<string> f)
    {
        try { return f() ?? ""; }
        catch { return ""; }
    }

    private static string ReadMachineGuid()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Cryptography");
        return key?.GetValue("MachineGuid")?.ToString() ?? "";
    }

    private static string ReadBiosSerial() => WmiScalar("Win32_BIOS", "SerialNumber");
    private static string ReadBoardSerial() => WmiScalar("Win32_BaseBoard", "SerialNumber");
    private static string ReadCpuId() => WmiScalar("Win32_Processor", "ProcessorId");

    private static string WmiScalar(string cls, string prop)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {prop} FROM {cls}");
            foreach (ManagementObject obj in searcher.Get())
            {
                var v = obj[prop]?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(v) &&
                    !v.Equals("To be filled by O.E.M.", StringComparison.OrdinalIgnoreCase) &&
                    !v.Equals("None", StringComparison.OrdinalIgnoreCase) &&
                    !v.Equals("Default string", StringComparison.OrdinalIgnoreCase))
                    return v;
            }
        }
        catch { }
        return "";
    }
}

/// <summary>HMAC-SHA256 signing for activation tokens (mock + local integrity).</summary>
public static class ActivationCrypto
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// Signing material is product+fingerprint bound. Portal-issued signatures are stored
    /// as-is; local mock signs with the same scheme so unsigned blobs are rejected.
    /// </summary>
    public static string Sign(ActivationClaims claims, string fingerprintHash, string productId)
    {
        var payload = CanonicalJson(claims);
        using var hmac = new HMACSHA256(DeriveKey(fingerprintHash, productId));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    public static bool Verify(ActivationToken token, string fingerprintHash, string productId)
    {
        if (token.Claims is null || string.IsNullOrWhiteSpace(token.Signature))
            return false;
        var expected = Sign(token.Claims, fingerprintHash, productId);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(token.Signature.ToLowerInvariant()));
    }

    public static string CanonicalJson(ActivationClaims claims) =>
        JsonSerializer.Serialize(claims, JsonOpts);

    private static byte[] DeriveKey(string fingerprintHash, string productId)
    {
        // Not a public secret — binds token to this machine + product. Portal may replace
        // with asymmetric verification in a future shared SDK revision.
        var material = Encoding.UTF8.GetBytes($"MBT-ACT-V1|{productId}|{fingerprintHash}");
        return SHA256.HashData(material);
    }
}

public static class PlatformJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
    public static T? Deserialize<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, Options);
}
