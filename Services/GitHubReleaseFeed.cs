using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CpuTempWidget.Services;

/// <summary>
/// Public fleet feed is <see cref="Branding.GitHubReleasesRepo"/> (installer only).
/// <see cref="Branding.GitHubSourceRepo"/> is the legacy fallback so already-installed
/// copies keep updating, and so <c>MBT_GITHUB_TOKEN</c> / <c>GH_TOKEN</c> can still
/// read a private source repo. Anonymous requests go first: a token scoped to one
/// repo must not turn a public 200 into a 404.
/// </summary>
public static class GitHubReleaseFeed
{
    public static IReadOnlyList<string> DefaultRepos { get; } =
    [
        Branding.GitHubReleasesRepo,
        Branding.GitHubSourceRepo
    ];

    public enum FeedProbeKind
    {
        Candidate,
        NotNewer,
        Miss
    }

    public readonly record struct FeedProbe(string Repo, FeedProbeKind Kind, UpdateCandidate? Candidate);

    public readonly record struct FeedDecision(UpdatePhase Phase, UpdateCandidate? Candidate, string Detail);

    /// <summary>
    /// <paramref name="overrideRepo"/> (MBT_GITHUB_REPO) replaces the feed with one repo.
    /// Empty means the public releases repo, then the source repo.
    /// </summary>
    public static IReadOnlyList<string> ResolveRepos(string? overrideRepo)
    {
        if (!string.IsNullOrWhiteSpace(overrideRepo))
        {
            var one = NormalizeRepo(overrideRepo);
            if (!string.IsNullOrWhiteSpace(one))
                return [one];
        }

        return DefaultRepos;
    }

    public static string NormalizeRepo(string repo)
    {
        var cleaned = (repo ?? "").Trim().Trim('/');
        if (cleaned.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned["https://github.com/".Length..];
        else if (cleaned.StartsWith("http://github.com/", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned["http://github.com/".Length..];
        if (cleaned.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[..^4];
        var parts = cleaned.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
            return $"{parts[0]}/{parts[1]}";
        return cleaned;
    }

    /// <summary>MBT_GITHUB_TOKEN, then GH_TOKEN, then GITHUB_TOKEN. Blank values are ignored.</summary>
    public static string? ReadToken()
    {
        foreach (var name in new[] { "MBT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    public static bool ShouldRetryWithToken(HttpStatusCode status) =>
        status is HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.NotFound
            or (HttpStatusCode)429;

    /// <summary>
    /// GET <paramref name="url"/> with no Authorization header, then retry once with
    /// <paramref name="token"/> when GitHub answers 401, 403, 404, or 429.
    /// The token is attached only for github.com and api.github.com.
    /// </summary>
    public static async Task<HttpResponseMessage> GetAsync(
        HttpClient http,
        string url,
        string? token,
        HttpCompletionOption completion,
        CancellationToken ct)
    {
        var anonymous = await SendAsync(http, url, null, completion, ct).ConfigureAwait(false);
        if (anonymous.IsSuccessStatusCode
            || string.IsNullOrWhiteSpace(token)
            || !IsGitHubUrl(url)
            || !ShouldRetryWithToken(anonymous.StatusCode))
            return anonymous;

        anonymous.Dispose();
        return await SendAsync(http, url, token.Trim(), completion, ct).ConfigureAwait(false);
    }

    public static IEnumerable<string> DownloadUrlAttempts(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            yield break;
        yield return url;
        var alt = LegacyReleaseDownloadFallback(url);
        if (!string.IsNullOrWhiteSpace(alt)
            && !string.Equals(alt, url, StringComparison.OrdinalIgnoreCase))
            yield return alt;
    }

    /// <summary>
    /// Portal rows and older notes may still point at
    /// github.com/MugoJnr/pulse/releases/download/... which 404s once that repo is private.
    /// </summary>
    public static string? LegacyReleaseDownloadFallback(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return null;

        const string prefix = "/MugoJnr/pulse/releases/download/";
        if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var rest = uri.AbsolutePath[prefix.Length..];
        var builder = new UriBuilder(uri)
        {
            Path = "/MugoJnr/pulse-releases/releases/download/" + rest
        };
        return builder.Uri.AbsoluteUri;
    }

    public static FeedProbeKind ParseLatest(string json, string currentVersion, string repo, out UpdateCandidate? candidate)
    {
        candidate = null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var root = doc.RootElement;
            var draft = root.TryGetProperty("draft", out var draftProp) && draftProp.ValueKind == JsonValueKind.True;
            var prerelease = root.TryGetProperty("prerelease", out var preProp) && preProp.ValueKind == JsonValueKind.True;
            if (draft || prerelease)
                return FeedProbeKind.Miss;

            var tag = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? "" : "";
            var version = tag.TrimStart('v', 'V');
            if (string.IsNullOrWhiteSpace(version))
                return FeedProbeKind.Miss;
            if (!IsNewerVersion(version, currentVersion))
                return FeedProbeKind.NotNewer;
            if (!TryPickAsset(root, out var assetUrl, out var assetName, out var assetSize, out var digest))
                return FeedProbeKind.Miss;

            var sha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? digest[7..]
                : digest;
            var notes = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";
            var published = root.TryGetProperty("published_at", out var publishedProp)
                            && DateTimeOffset.TryParse(publishedProp.GetString(), out var publishedAt)
                ? publishedAt
                : DateTimeOffset.UtcNow;

            candidate = new UpdateCandidate
            {
                Version = version,
                Tag = tag,
                AssetUrl = assetUrl,
                AssetName = assetName,
                SizeBytes = assetSize,
                Sha256 = sha256,
                ReleaseNotes = notes,
                PublishedAt = published,
                IsPrerelease = false,
                Repo = repo
            };
            return FeedProbeKind.Candidate;
        }
        catch (JsonException)
        {
            return FeedProbeKind.Miss;
        }
    }

    /// <summary>
    /// Newest installer across the repos that answered. Equal versions keep the earlier
    /// probe, which is the public releases repo when both published the same build.
    /// </summary>
    public static FeedDecision Decide(IReadOnlyList<FeedProbe> probes)
    {
        UpdateCandidate? best = null;
        var anyCurrent = false;
        foreach (var probe in probes)
        {
            if (probe.Kind == FeedProbeKind.NotNewer)
                anyCurrent = true;
            if (probe.Kind != FeedProbeKind.Candidate || probe.Candidate is null)
                continue;
            if (best is null || IsNewerVersion(probe.Candidate.Version, best.Version))
                best = probe.Candidate;
        }

        if (best is not null)
        {
            var repo = string.IsNullOrWhiteSpace(best.Repo) ? "GitHub" : best.Repo;
            return new FeedDecision(UpdatePhase.Available, best, $"update available: {best.Version} from {repo}");
        }

        if (anyCurrent)
            return new FeedDecision(UpdatePhase.UpToDate, null, "up to date");

        return new FeedDecision(UpdatePhase.Failed, null, "GitHub release check failed");
    }

    public static bool IsNewerVersion(string candidate, string current)
    {
        try
        {
            return NormalizeVersion(candidate) > NormalizeVersion(current);
        }
        catch
        {
            return !string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient http,
        string url,
        string? token,
        HttpCompletionOption completion,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("MugoByte-Pulse-Updater");
        if (!string.IsNullOrWhiteSpace(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(req, completion, ct).ConfigureAwait(false);
    }

    /// <summary>https://github.com and https://api.github.com only. Tokens are never sent anywhere else.</summary>
    public static bool IsGitHubUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;
        return uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
               || uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryPickAsset(
        JsonElement root,
        out string assetUrl,
        out string assetName,
        out long assetSize,
        out string digest)
    {
        assetUrl = "";
        assetName = "";
        assetSize = 0;
        digest = "";
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return false;

        string? fallbackUrl = null;
        string? fallbackName = null;
        long fallbackSize = 0;
        string? fallbackDigest = null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;
            var url = asset.TryGetProperty("browser_download_url", out var urlProp) ? urlProp.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(url))
                continue;
            var size = asset.TryGetProperty("size", out var sizeProp) && sizeProp.TryGetInt64(out var parsedSize)
                ? parsedSize
                : 0;
            var assetDigest = asset.TryGetProperty("digest", out var digestProp) ? digestProp.GetString() ?? "" : "";

            if (name.Equals("Pulse-Setup.exe", StringComparison.OrdinalIgnoreCase))
            {
                assetUrl = url;
                assetName = name;
                assetSize = size;
                digest = assetDigest;
                return true;
            }

            if (fallbackUrl is null)
            {
                fallbackUrl = url;
                fallbackName = name;
                fallbackSize = size;
                fallbackDigest = assetDigest;
            }
        }

        if (fallbackUrl is null)
            return false;
        assetUrl = fallbackUrl;
        assetName = fallbackName ?? "";
        assetSize = fallbackSize;
        digest = fallbackDigest ?? "";
        return true;
    }

    private static Version NormalizeVersion(string raw)
    {
        var cleaned = new string(raw.Trim().TrimStart('v', 'V')
            .TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
        if (string.IsNullOrWhiteSpace(cleaned))
            return new Version(0, 0);
        if (!cleaned.Contains('.'))
            cleaned += ".0";
        return Version.Parse(cleaned);
    }
}
