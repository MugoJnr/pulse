using System.IO;
using System.Security.Cryptography;
using MugoByte.Platform;

namespace CpuTempWidget.Services;

/// <summary>Dev/E2E-only mock update overrides parsed from process args.</summary>
public static class UpdateE2EOptions
{
    public static string? MockUpdateUrl { get; private set; }
    public static string? MockUpdateVersion { get; private set; }
    public static bool SelfUpdateTest { get; private set; }

    public static void Parse(string[]? args)
    {
        if (args is null || args.Length == 0) return;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Equals("--self-update-test", StringComparison.OrdinalIgnoreCase))
            {
                SelfUpdateTest = true;
                continue;
            }

            if (a.Equals("--auto-update-dry-run", StringComparison.OrdinalIgnoreCase))
            {
                UpdateService.DryRunInstall = true;
                continue;
            }

            if (a.StartsWith("--mock-update-url=", StringComparison.OrdinalIgnoreCase))
            {
                MockUpdateUrl = a["--mock-update-url=".Length..].Trim().Trim('"');
                continue;
            }

            if (a.Equals("--mock-update-url", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                MockUpdateUrl = args[++i].Trim().Trim('"');
                continue;
            }

            if (a.StartsWith("--mock-update-version=", StringComparison.OrdinalIgnoreCase))
            {
                MockUpdateVersion = a["--mock-update-version=".Length..].Trim().Trim('"');
                continue;
            }

            if (a.Equals("--mock-update-version", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                MockUpdateVersion = args[++i].Trim().Trim('"');
            }
        }
    }

    public static bool HasMock => !string.IsNullOrWhiteSpace(MockUpdateUrl);

    /// <summary>Build a portal-shaped update result from mock URL (dev/e2e only).</summary>
    public static async Task<UpdateCheckResult?> TryBuildMockResultAsync(CancellationToken ct = default)
    {
        if (!HasMock) return null;
        var url = MockUpdateUrl!;
        var version = string.IsNullOrWhiteSpace(MockUpdateVersion) ? "1.12.0" : MockUpdateVersion!;
        string checksum = "";
        try
        {
            if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                || (!url.Contains("://", StringComparison.Ordinal) && File.Exists(url)))
            {
                var path = url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                    ? new Uri(url).LocalPath
                    : url;
                if (File.Exists(path))
                {
                    await using var fs = File.OpenRead(path);
                    checksum = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
                }
            }
            else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                     || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                var bytes = await http.GetByteArrayAsync(url, ct);
                checksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            }
        }
        catch
        {
            // Still return available so E2E can exercise download path; checksum optional.
        }

        return new UpdateCheckResult
        {
            UpdateAvailable = true,
            LatestVersion = version,
            DownloadUrl = url,
            ChecksumSha256 = checksum,
            ReleaseNotes = "E2E mock update",
            Message = "Mock update from --mock-update-url"
        };
    }
}

/// <summary>Maps <see cref="UpdateCenter"/> GitHub checks into portal <see cref="UpdateCheckResult"/>.</summary>
public sealed class GitHubUpdateFallback : IUpdateFallback
{
    private readonly UpdateCenter _center;
    private readonly IPlatformLog _log;

    public GitHubUpdateFallback(UpdateCenter center, IPlatformLog log)
    {
        _center = center;
        _log = log;
    }

    public async Task<UpdateCheckResult?> TryCheckAsync(string currentVersion, CancellationToken ct = default)
    {
        try
        {
            var candidate = await _center.CheckAsync(currentVersion, ct).ConfigureAwait(false);
            if (candidate is null) return null;

            return new UpdateCheckResult
            {
                UpdateAvailable = true,
                LatestVersion = candidate.Version,
                DownloadUrl = candidate.AssetUrl,
                ChecksumSha256 = candidate.Sha256,
                ReleaseNotes = candidate.ReleaseNotes,
                Message = $"GitHub release {candidate.Tag}"
            };
        }
        catch (Exception ex)
        {
            _log.Warn("update", "GitHub fallback: " + ex.Message);
            return null;
        }
    }
}
