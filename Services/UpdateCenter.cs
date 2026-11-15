using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using MugoByte.Platform;

namespace CpuTempWidget.Services;

public enum UpdatePhase
{
    Idle,
    Checking,
    Available,
    Downloading,
    ReadyToInstall,
    Installing,
    Failed,
    UpToDate
}

public sealed class UpdateCandidate
{
    public string Version { get; init; } = "";
    public string Tag { get; init; } = "";
    public string AssetUrl { get; init; } = "";
    public string AssetName { get; init; } = "";
    public long SizeBytes { get; init; }
    public string Sha256 { get; init; } = "";
    public string ReleaseNotes { get; init; } = "";
    public DateTimeOffset PublishedAt { get; init; }
    public bool IsPrerelease { get; init; }
    /// <summary>owner/name of the GitHub repo this candidate was read from.</summary>
    public string Repo { get; init; } = "";
}

public sealed class UpdateCenter : IDisposable
{
    private readonly IPlatformLog _log;
    private readonly IReadOnlyList<string> _repos;
    private readonly HttpClient _http;
    private readonly string _updatesDir;
    private readonly object _gate = new();
    private CancellationTokenSource? _downloadCts;
    private UpdatePhase _phase = UpdatePhase.Idle;
    private UpdateCandidate? _candidate;
    private string? _downloadedPath;

    public UpdatePhase Phase => _phase;
    public UpdateCandidate? Candidate => _candidate;
    public string? DownloadedPath => _downloadedPath;
    public IReadOnlyList<string> Repos => _repos;

    public event Action<UpdatePhase, UpdateCandidate?>? PhaseChanged;
    public event Action<string>? Progress;

    public UpdateCenter(IPlatformLog log)
    {
        _log = log;
        _repos = GitHubReleaseFeed.ResolveRepos(Environment.GetEnvironmentVariable("MBT_GITHUB_REPO"));
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MugoByte", "Pulse", "updates");
        Directory.CreateDirectory(dir);
        _updatesDir = dir;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MugoByte-Pulse-Updater");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public void Dispose()
    {
        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
        _http.Dispose();
    }

    private void SetPhase(UpdatePhase phase)
    {
        lock (_gate)
        {
            if (_phase == phase) return;
            _phase = phase;
            try { PhaseChanged?.Invoke(phase, _candidate); } catch { }
        }
    }

    private void Report(string msg)
    {
        _log.Info("update", msg);
        try { Progress?.Invoke(msg); } catch { }
    }

    public async Task<UpdateCandidate?> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        SetPhase(UpdatePhase.Checking);
        try
        {
            Report("checking " + string.Join(", ", _repos));
            var probes = new List<GitHubReleaseFeed.FeedProbe>(_repos.Count);
            foreach (var repo in _repos)
            {
                ct.ThrowIfCancellationRequested();
                probes.Add(await ProbeRepoAsync(repo, currentVersion, ct).ConfigureAwait(false));
            }

            var decision = GitHubReleaseFeed.Decide(probes);
            lock (_gate)
            {
                _candidate = decision.Candidate;
            }

            SetPhase(decision.Phase);
            if (!string.IsNullOrWhiteSpace(decision.Detail))
                Report(decision.Detail);
            return decision.Candidate;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Report($"check failed: {ex.Message}");
            SetPhase(UpdatePhase.Failed);
            return null;
        }
    }

    private async Task<GitHubReleaseFeed.FeedProbe> ProbeRepoAsync(string repo, string currentVersion, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{repo}/releases/latest";
        try
        {
            using var res = await GitHubReleaseFeed.GetAsync(
                _http,
                url,
                GitHubReleaseFeed.ReadToken(),
                HttpCompletionOption.ResponseContentRead,
                ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                Report($"GitHub API {repo} {(int)res.StatusCode}");
                return new GitHubReleaseFeed.FeedProbe(repo, GitHubReleaseFeed.FeedProbeKind.Miss, null);
            }

            var json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var kind = GitHubReleaseFeed.ParseLatest(json, currentVersion, repo, out var candidate);
            return new GitHubReleaseFeed.FeedProbe(repo, kind, candidate);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Report($"GitHub API {repo} failed: {ex.Message}");
            return new GitHubReleaseFeed.FeedProbe(repo, GitHubReleaseFeed.FeedProbeKind.Miss, null);
        }
    }

    private async Task<HttpResponseMessage?> DownloadReleaseAsync(string assetUrl, CancellationToken ct)
    {
        HttpResponseMessage? last = null;
        foreach (var target in GitHubReleaseFeed.DownloadUrlAttempts(assetUrl))
        {
            last?.Dispose();
            last = await GitHubReleaseFeed.GetAsync(
                _http,
                target,
                GitHubReleaseFeed.ReadToken(),
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);
            if (last.IsSuccessStatusCode)
                return last;
            Report($"download failed: {(int)last.StatusCode} {target}");
        }

        last?.Dispose();
        return null;
    }

    public async Task<bool> DownloadAsync(IProgress<(long received, long total)>? progress = null, CancellationToken ct = default)
    {
        UpdateCandidate candidate;
        lock (_gate)
        {
            if (_phase != UpdatePhase.Available && _phase != UpdatePhase.Downloading) return false;
            candidate = _candidate!;
        }

        if (string.IsNullOrWhiteSpace(candidate.AssetUrl) || string.IsNullOrWhiteSpace(candidate.AssetName))
            return false;

        var finalPath = Path.Combine(_updatesDir, candidate.AssetName);
        var partPath = finalPath + ".part";

        // Supersede: if a newer candidate exists, cancel old download
        var oldCts = Interlocked.Exchange(ref _downloadCts, new CancellationTokenSource());
        oldCts?.Cancel();
        oldCts?.Dispose();

        // Delete stale part for same name
        try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }

        SetPhase(UpdatePhase.Downloading);
        Report($"downloading {candidate.AssetName} ({FormatBytes(candidate.SizeBytes)})");

        try
        {
            using var res = await DownloadReleaseAsync(candidate.AssetUrl, ct).ConfigureAwait(false);
            if (res is null)
            {
                SetPhase(UpdatePhase.Failed);
                return false;
            }

            var total = res.Content.Headers.ContentLength ?? candidate.SizeBytes;
            using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var fs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long received = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // Supersede check mid-download
                UpdateCandidate current;
                lock (_gate)
                {
                    current = _candidate!;
                }
                if (current != candidate)
                {
                    Report("superseded by newer candidate — aborting");
                    try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
                    SetPhase(UpdatePhase.Idle);
                    return false;
                }

                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0) break;
                await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                progress?.Report((received, total));
            }

            // SHA-256 verify
            if (!string.IsNullOrWhiteSpace(candidate.Sha256))
            {
                var actual = await ComputeSha256Async(partPath, ct);
                if (!string.Equals(actual, candidate.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    Report("checksum mismatch — discarding");
                    try { File.Delete(partPath); } catch { }
                    SetPhase(UpdatePhase.Failed);
                    return false;
                }
                Report("checksum OK");
            }

            // Promote
            try
            {
                if (File.Exists(finalPath)) File.Delete(finalPath);
                File.Move(partPath, finalPath);
            }
            catch (Exception ex)
            {
                Report($"promote failed: {ex.Message}");
                SetPhase(UpdatePhase.Failed);
                return false;
            }

            lock (_gate)
            {
                _downloadedPath = finalPath;
            }
            CleanupStaleKeep(finalPath);
            SetPhase(UpdatePhase.ReadyToInstall);
            Report("ready to install: " + finalPath);
            return true;
        }
        catch (OperationCanceledException)
        {
            Report("download cancelled");
            SetPhase(UpdatePhase.Idle);
            return false;
        }
        catch (Exception ex)
        {
            Report($"download error: {ex.Message}");
            SetPhase(UpdatePhase.Failed);
            return false;
        }
    }

    public async Task<bool> InstallNowAsync()
    {
        string path;
        lock (_gate)
        {
            if (_phase != UpdatePhase.ReadyToInstall || string.IsNullOrWhiteSpace(_downloadedPath))
                return false;
            _phase = UpdatePhase.Installing;
            path = _downloadedPath!;
        }

        try
        {
            Report("launching installer: " + path);
            var psi = new ProcessStartInfo(path) { UseShellExecute = true };
            Process.Start(psi);
            return true; // caller should shutdown app
        }
        catch (Exception ex)
        {
            Report($"install launch failed: {ex.Message}");
            SetPhase(UpdatePhase.Failed);
            return false;
        }
    }

    public void CleanupStale()
    {
        try
        {
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (_gate)
            {
                if (!string.IsNullOrWhiteSpace(_downloadedPath))
                    keep.Add(Path.GetFileName(_downloadedPath));
                if (_candidate != null)
                    keep.Add(_candidate.AssetName);
            }

            foreach (var f in Directory.EnumerateFiles(_updatesDir, "*.exe"))
            {
                if (!keep.Contains(Path.GetFileName(f)))
                {
                    try { File.Delete(f); } catch { }
                }
            }
            foreach (var f in Directory.EnumerateFiles(_updatesDir, "*.part"))
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    private void CleanupStaleKeep(string current)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_updatesDir, "*.exe"))
            {
                if (!string.Equals(f, current, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(f); } catch { }
            }
            foreach (var f in Directory.EnumerateFiles(_updatesDir, "*.part"))
            {
                if (!string.Equals(f, current + ".part", StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    private async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string FormatBytes(long bytes)
    {
        string[] suf = { "B", "KB", "MB", "GB" };
        int i = 0;
        double d = bytes;
        while (d >= 1024 && i < suf.Length - 1) { d /= 1024; i++; }
        return $"{d:0.#} {suf[i]}";
    }
}