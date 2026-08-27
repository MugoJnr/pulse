using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
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
}

public sealed class UpdateCenter : IDisposable
{
    private readonly IPlatformLog _log;
    private readonly string _repo;
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

    public event Action<UpdatePhase, UpdateCandidate?>? PhaseChanged;
    public event Action<string>? Progress;

    public UpdateCenter(IPlatformLog log)
    {
        _log = log;
        _repo = Environment.GetEnvironmentVariable("MBT_GITHUB_REPO")?.Trim()
                ?? Branding.GitHubRepo;
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

    // ── Version helpers (mirror PortalClients) ───────────────────────────────────
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

    // ── Public API ────────────────────────────────────────────────────────────────

    private static string NormalizeGitHubRepo(string repo)
    {
        var cleaned = (repo ?? "").Trim().Trim('/');
        // Accept full URLs or owner/name; never EscapeDataString the slash (breaks API path).
        if (cleaned.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned["https://github.com/".Length..];
        if (cleaned.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[..^4];
        var parts = cleaned.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
            return $"{parts[0]}/{parts[1]}";
        return cleaned;
    }

    private void ApplyGitHubAuth(HttpRequestMessage req)
    {
        var token = Environment.GetEnvironmentVariable("MBT_GITHUB_TOKEN")
                    ?? Environment.GetEnvironmentVariable("GH_TOKEN")
                    ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrWhiteSpace(token)) return;
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
    }

    public async Task<UpdateCandidate?> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        SetPhase(UpdatePhase.Checking);
        try
        {
            var repo = NormalizeGitHubRepo(_repo);
            var url = $"https://api.github.com/repos/{repo}/releases/latest";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            ApplyGitHubAuth(req);

            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                Report($"GitHub API {res.StatusCode}");
                SetPhase(UpdatePhase.Failed);
                return null;
            }

            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var draft = root.TryGetProperty("draft", out var draftProp) && draftProp.GetBoolean();
            var prerelease = root.TryGetProperty("prerelease", out var p) && p.GetBoolean();
            if (draft || prerelease)
            {
                SetPhase(UpdatePhase.UpToDate);
                return null;
            }

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var version = tag.TrimStart('v', 'V');
            if (string.IsNullOrWhiteSpace(version) || !IsNewerVersion(version, currentVersion))
            {
                SetPhase(UpdatePhase.UpToDate);
                return null;
            }

            // Pick the installer asset (self-contained .exe)
            string assetUrl = "", assetName = "";
            long assetSize = 0;
            string digest = "";

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    var urlProp = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                    var sz = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    var digestProp = a.TryGetProperty("digest", out var di) ? di.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(urlProp)) continue;
                    assetUrl = urlProp;
                    assetName = name;
                    assetSize = sz;
                    digest = digestProp; // "sha256:ABC..."
                    break; // first .exe
                }
            }

            if (string.IsNullOrWhiteSpace(assetUrl))
            {
                Report("no installer asset found");
                SetPhase(UpdatePhase.Failed);
                return null;
            }

            string sha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? digest[7..] : digest;

            var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            var published = root.TryGetProperty("published_at", out var pa) && DateTimeOffset.TryParse(pa.GetString(), out var dt)
                ? dt : DateTimeOffset.UtcNow;

            var candidate = new UpdateCandidate
            {
                Version = version,
                Tag = tag,
                AssetUrl = assetUrl,
                AssetName = assetName,
                SizeBytes = assetSize,
                Sha256 = sha256,
                ReleaseNotes = notes,
                PublishedAt = published,
                IsPrerelease = prerelease
            };

            lock (_gate)
            {
                _candidate = candidate;
            }

            SetPhase(UpdatePhase.Available);
            Report($"update available: {version}");
            return candidate;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Report($"check failed: {ex.Message}");
            SetPhase(UpdatePhase.Failed);
            return null;
        }
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
            using var req = new HttpRequestMessage(HttpMethod.Get, candidate.AssetUrl);
            ApplyGitHubAuth(req);
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode)
            {
                Report($"download failed: {res.StatusCode}");
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

// Lightweight GitHub Release DTO for CheckAsync
internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("assets")] public GitHubAsset[] Assets { get; set; } = Array.Empty<GitHubAsset>();
    [JsonPropertyName("body")] public string Body { get; set; } = "";
    [JsonPropertyName("published_at")] public string PublishedAt { get; set; } = "";
}

internal sealed class GitHubAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("digest")] public string Digest { get; set; } = "";
}