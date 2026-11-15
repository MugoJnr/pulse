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
        // A machine with a thermal zone publishes a source on the first probe.
        // A headless runner publishes "unavailable" only after three failed probes (~9s).
        SystemMonitor.EnsureSampler();
        using var mon = new SystemMonitor();
        _ = mon.Read();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        string? src = null;
        while (DateTime.UtcNow < deadline)
        {
            src = SystemMonitor.LastTemperatureSource;
            if (!string.IsNullOrWhiteSpace(src))
                return;
            await Task.Delay(250);
        }

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

public class UpdateResolverTests
{
    [Fact]
    public void Merge_PrefersGitHubWhenPortalUnauthorizedButGitHubHasRelease()
    {
        var log = new ListPlatformLog();
        var portal = new UpdateCheckResult { NeedsAuthRefresh = true, Message = "unauthorized" };
        var gh = new UpdateCheckResult
        {
            UpdateAvailable = true,
            LatestVersion = "1.13.0",
            DownloadUrl = "https://github.com/MugoJnr/pulse-releases/releases/download/v1.13.0/Pulse-Setup.exe"
        };
        var merged = UpdateResolver.Merge("1.12.0", portal, gh, log);
        Assert.True(merged.UpdateAvailable);
        Assert.Equal("1.13.0", merged.LatestVersion);
    }

    [Fact]
    public void Merge_PrefersNewerVersionWhenBothHaveUpdates()
    {
        var log = new ListPlatformLog();
        var portal = new UpdateCheckResult
        {
            UpdateAvailable = true,
            LatestVersion = "1.12.5",
            DownloadUrl = "https://portal.example/pulse-1.12.5.exe"
        };
        var gh = new UpdateCheckResult
        {
            UpdateAvailable = true,
            LatestVersion = "1.13.0",
            DownloadUrl = "https://github.com/example/pulse-1.13.0.exe"
        };
        var merged = UpdateResolver.Merge("1.12.0", portal, gh, log);
        Assert.Equal("1.13.0", merged.LatestVersion);
    }
}

internal sealed class ListPlatformLog : IPlatformLog
{
    public void Info(string category, string message) { }
    public void Warn(string category, string message) { }
    public void Error(string category, string message) { }
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

    [Fact]
    public void ShouldAutoInstall_RespectsSettingsAndMandatory()
    {
        var update = new UpdateCheckResult
        {
            UpdateAvailable = true,
            LatestVersion = "9.9.9",
            DownloadUrl = "http://127.0.0.1/Pulse-Setup.exe"
        };
        var off = new CpuTempWidget.Models.AppSettings { AutoCheckUpdates = true, AutoInstallUpdates = false };
        Assert.False(UpdateService.ShouldAutoInstall(update, off));

        var on = new CpuTempWidget.Models.AppSettings { AutoCheckUpdates = true, AutoInstallUpdates = true };
        Assert.True(UpdateService.ShouldAutoInstall(update, on));

        var mandatory = new UpdateCheckResult
        {
            UpdateAvailable = true,
            LatestVersion = "9.9.9",
            DownloadUrl = "http://127.0.0.1/x.exe",
            IsMandatory = true
        };
        Assert.True(UpdateService.ShouldAutoInstall(mandatory, off));
    }
}

public class GitHubReleaseFeedTests
{
    [Fact]
    public void ResolveRepos_Default_ChecksPublicFeedThenSource()
    {
        Assert.Equal(
            new[] { "MugoJnr/pulse-releases", "MugoJnr/pulse" },
            GitHubReleaseFeed.ResolveRepos(null));
        Assert.Equal(
            new[] { "MugoJnr/pulse-releases", "MugoJnr/pulse" },
            GitHubReleaseFeed.ResolveRepos("  "));
    }

    [Fact]
    public void ResolveRepos_Override_IsSingleRepo()
    {
        var repos = GitHubReleaseFeed.ResolveRepos("https://github.com/MugoJnr/pulse.git");
        Assert.Equal(new[] { "MugoJnr/pulse" }, repos);
    }

    [Fact]
    public void NormalizeRepo_AcceptsOwnerName()
    {
        Assert.Equal("MugoJnr/pulse-releases", GitHubReleaseFeed.NormalizeRepo("MugoJnr/pulse-releases/"));
        Assert.Equal("MugoJnr/pulse-releases", GitHubReleaseFeed.NormalizeRepo("https://github.com/MugoJnr/pulse-releases.git"));
    }

    [Fact]
    public void ReadToken_PrefersMbtThenGhThenActions()
    {
        var prevM = Environment.GetEnvironmentVariable("MBT_GITHUB_TOKEN");
        var prevG = Environment.GetEnvironmentVariable("GH_TOKEN");
        var prevA = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("MBT_GITHUB_TOKEN", " mbt ");
            Environment.SetEnvironmentVariable("GH_TOKEN", "gh");
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", "actions");
            Assert.Equal("mbt", GitHubReleaseFeed.ReadToken());

            Environment.SetEnvironmentVariable("MBT_GITHUB_TOKEN", " ");
            Assert.Equal("gh", GitHubReleaseFeed.ReadToken());

            Environment.SetEnvironmentVariable("GH_TOKEN", null);
            Assert.Equal("actions", GitHubReleaseFeed.ReadToken());
        }
        finally
        {
            Environment.SetEnvironmentVariable("MBT_GITHUB_TOKEN", prevM);
            Environment.SetEnvironmentVariable("GH_TOKEN", prevG);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", prevA);
        }
    }

    [Fact]
    public void ShouldRetryWithToken_OnlyAuthAndMissing()
    {
        Assert.True(GitHubReleaseFeed.ShouldRetryWithToken(System.Net.HttpStatusCode.NotFound));
        Assert.True(GitHubReleaseFeed.ShouldRetryWithToken(System.Net.HttpStatusCode.Unauthorized));
        Assert.True(GitHubReleaseFeed.ShouldRetryWithToken(System.Net.HttpStatusCode.Forbidden));
        Assert.True(GitHubReleaseFeed.ShouldRetryWithToken((System.Net.HttpStatusCode)429));
        Assert.False(GitHubReleaseFeed.ShouldRetryWithToken(System.Net.HttpStatusCode.OK));
        Assert.False(GitHubReleaseFeed.ShouldRetryWithToken(System.Net.HttpStatusCode.InternalServerError));
    }

    [Fact]
    public async Task GetAsync_DoesNotSendTokenWhenAnonymousSucceeds()
    {
        var handler = new RecordingHandler();
        handler.Enqueue(System.Net.HttpStatusCode.OK, "{}");
        using var http = new System.Net.Http.HttpClient(handler);
        using var res = await GitHubReleaseFeed.GetAsync(
            http,
            "https://api.github.com/repos/MugoJnr/pulse-releases/releases/latest",
            "scoped-to-pulse-only",
            System.Net.Http.HttpCompletionOption.ResponseContentRead,
            CancellationToken.None);
        Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);
        Assert.Single(handler.Calls);
        Assert.Null(handler.Calls[0].Auth);
    }

    [Fact]
    public async Task GetAsync_RetriesWithTokenAfterNotFound()
    {
        var handler = new RecordingHandler();
        handler.Enqueue(System.Net.HttpStatusCode.NotFound, "");
        handler.Enqueue(System.Net.HttpStatusCode.OK, "{}");
        using var http = new System.Net.Http.HttpClient(handler);
        using var res = await GitHubReleaseFeed.GetAsync(
            http,
            "https://api.github.com/repos/MugoJnr/pulse/releases/latest",
            "mbt-token",
            System.Net.Http.HttpCompletionOption.ResponseContentRead,
            CancellationToken.None);
        Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(2, handler.Calls.Count);
        Assert.Null(handler.Calls[0].Auth);
        Assert.Equal("mbt-token", handler.Calls[1].Auth);
    }

    [Fact]
    public async Task GetAsync_DoesNotAttachTokenToPlainHttp()
    {
        var handler = new RecordingHandler();
        handler.Enqueue(System.Net.HttpStatusCode.NotFound, "");
        using var http = new System.Net.Http.HttpClient(handler);
        using var res = await GitHubReleaseFeed.GetAsync(
            http,
            "http://github.com/MugoJnr/pulse/releases/download/v1.13.0/Pulse-Setup.exe",
            "mbt-token",
            System.Net.Http.HttpCompletionOption.ResponseContentRead,
            CancellationToken.None);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, res.StatusCode);
        Assert.Single(handler.Calls);
        Assert.Null(handler.Calls[0].Auth);
    }

    [Fact]
    public async Task GetAsync_DoesNotAttachTokenToOtherHosts()
    {
        var handler = new RecordingHandler();
        handler.Enqueue(System.Net.HttpStatusCode.NotFound, "");
        using var http = new System.Net.Http.HttpClient(handler);
        using var res = await GitHubReleaseFeed.GetAsync(
            http,
            "https://portal.mugobyte.com/api/cloud/updates",
            "mbt-token",
            System.Net.Http.HttpCompletionOption.ResponseContentRead,
            CancellationToken.None);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, res.StatusCode);
        Assert.Single(handler.Calls);
        Assert.Null(handler.Calls[0].Auth);
    }

    [Fact]
    public void LegacyDownload_RewritesSourceRepoReleaseUrlOnly()
    {
        var alt = GitHubReleaseFeed.LegacyReleaseDownloadFallback(
            "https://github.com/MugoJnr/pulse/releases/download/v1.13.0/Pulse-Setup.exe");
        Assert.Equal(
            "https://github.com/MugoJnr/pulse-releases/releases/download/v1.13.0/Pulse-Setup.exe",
            alt);
        Assert.Null(GitHubReleaseFeed.LegacyReleaseDownloadFallback(
            "https://github.com/MugoJnr/pulse-releases/releases/download/v1.13.0/Pulse-Setup.exe"));
        Assert.Null(GitHubReleaseFeed.LegacyReleaseDownloadFallback("https://portal.mugobyte.com/pulse.exe"));
    }

    [Fact]
    public void ParseLatest_PrefersPulseSetupAndStripsSha256()
    {
        const string json = """
            {
              "tag_name": "v1.13.0",
              "draft": false,
              "prerelease": false,
              "body": "notes",
              "published_at": "2026-11-15T00:00:00Z",
              "assets": [
                {"name": "Other.exe", "browser_download_url": "https://github.com/MugoJnr/pulse-releases/releases/download/v1.13.0/Other.exe", "size": 1, "digest": "sha256:aa"},
                {"name": "Pulse-Setup.exe", "browser_download_url": "https://github.com/MugoJnr/pulse-releases/releases/download/v1.13.0/Pulse-Setup.exe", "size": 70, "digest": "sha256:abc"}
              ]
            }
            """;
        var kind = GitHubReleaseFeed.ParseLatest(json, "1.12.4", "MugoJnr/pulse-releases", out var candidate);
        Assert.Equal(GitHubReleaseFeed.FeedProbeKind.Candidate, kind);
        Assert.NotNull(candidate);
        Assert.Equal("Pulse-Setup.exe", candidate!.AssetName);
        Assert.Equal("abc", candidate.Sha256);
        Assert.Equal("1.13.0", candidate.Version);
        Assert.Equal("MugoJnr/pulse-releases", candidate.Repo);
    }

    [Fact]
    public void ParseLatest_NotNewer_IsCurrent()
    {
        const string json = """
            {"tag_name":"v1.12.4","draft":false,"prerelease":false,"assets":[{"name":"Pulse-Setup.exe","browser_download_url":"https://github.com/MugoJnr/pulse-releases/releases/download/v1.12.4/Pulse-Setup.exe","size":1}]}
            """;
        var kind = GitHubReleaseFeed.ParseLatest(json, "1.12.4", "MugoJnr/pulse-releases", out var candidate);
        Assert.Equal(GitHubReleaseFeed.FeedProbeKind.NotNewer, kind);
        Assert.Null(candidate);
    }

    [Fact]
    public void Decide_UsesPublicFeedWhenSourceIsMissing()
    {
        var decision = GitHubReleaseFeed.Decide(
        [
            new GitHubReleaseFeed.FeedProbe("MugoJnr/pulse-releases", GitHubReleaseFeed.FeedProbeKind.Candidate, new UpdateCandidate
            {
                Version = "1.13.0",
                AssetUrl = "https://github.com/MugoJnr/pulse-releases/releases/download/v1.13.0/Pulse-Setup.exe",
                Repo = "MugoJnr/pulse-releases"
            }),
            new GitHubReleaseFeed.FeedProbe("MugoJnr/pulse", GitHubReleaseFeed.FeedProbeKind.Miss, null)
        ]);
        Assert.Equal(UpdatePhase.Available, decision.Phase);
        Assert.Equal("1.13.0", decision.Candidate!.Version);
        Assert.Contains("pulse-releases", decision.Detail);
    }

    [Fact]
    public void Decide_FallsBackToSourceWhenPublicFeedIsEmpty()
    {
        var decision = GitHubReleaseFeed.Decide(
        [
            new GitHubReleaseFeed.FeedProbe("MugoJnr/pulse-releases", GitHubReleaseFeed.FeedProbeKind.Miss, null),
            new GitHubReleaseFeed.FeedProbe("MugoJnr/pulse", GitHubReleaseFeed.FeedProbeKind.Candidate, new UpdateCandidate
            {
                Version = "1.12.4",
                Repo = "MugoJnr/pulse"
            })
        ]);
        Assert.Equal("1.12.4", decision.Candidate!.Version);
        Assert.Contains("MugoJnr/pulse", decision.Detail);
    }

    [Fact]
    public void Decide_PicksNewerAcrossRepos_AndTreatsEqualAsPublic()
    {
        var newer = GitHubReleaseFeed.Decide(
        [
            Probe("MugoJnr/pulse-releases", "1.12.5"),
            Probe("MugoJnr/pulse", "1.13.0")
        ]);
        Assert.Equal("1.13.0", newer.Candidate!.Version);

        var tie = GitHubReleaseFeed.Decide(
        [
            Probe("MugoJnr/pulse-releases", "1.13.0"),
            Probe("MugoJnr/pulse", "1.13.0")
        ]);
        Assert.Equal("MugoJnr/pulse-releases", tie.Candidate!.Repo);
    }

    [Fact]
    public void Decide_UpToDateWhenARepoAnswersAndNoneAreNewer()
    {
        var decision = GitHubReleaseFeed.Decide(
        [
            new GitHubReleaseFeed.FeedProbe("MugoJnr/pulse-releases", GitHubReleaseFeed.FeedProbeKind.NotNewer, null),
            new GitHubReleaseFeed.FeedProbe("MugoJnr/pulse", GitHubReleaseFeed.FeedProbeKind.Miss, null)
        ]);
        Assert.Equal(UpdatePhase.UpToDate, decision.Phase);
        Assert.Null(decision.Candidate);
    }

    [Fact]
    public void Decide_FailedWhenEveryRepoMisses()
    {
        var decision = GitHubReleaseFeed.Decide(
        [
            new GitHubReleaseFeed.FeedProbe("MugoJnr/pulse-releases", GitHubReleaseFeed.FeedProbeKind.Miss, null),
            new GitHubReleaseFeed.FeedProbe("MugoJnr/pulse", GitHubReleaseFeed.FeedProbeKind.Miss, null)
        ]);
        Assert.Equal(UpdatePhase.Failed, decision.Phase);
    }

    private static GitHubReleaseFeed.FeedProbe Probe(string repo, string version) =>
        new(repo, GitHubReleaseFeed.FeedProbeKind.Candidate, new UpdateCandidate { Version = version, Repo = repo });

    private sealed class RecordingHandler : System.Net.Http.HttpMessageHandler
    {
        public List<(string? Auth, string Url)> Calls { get; } = new();
        private readonly Queue<(System.Net.HttpStatusCode Code, string Body)> _responses = new();

        public void Enqueue(System.Net.HttpStatusCode code, string body) => _responses.Enqueue((code, body));

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls.Add((request.Headers.Authorization?.Parameter, request.RequestUri!.ToString()));
            var (code, body) = _responses.Dequeue();
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(code)
            {
                Content = new System.Net.Http.StringContent(body)
            });
        }
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
