namespace MugoByte.Platform;

/// <summary>
/// Fleet update resolution: Portal when available, always merge with public GitHub Releases.
/// Every Pulse PC should receive updates without per-machine tokens when GitHub releases are public.
/// </summary>
public static class UpdateResolver
{
    public static async Task<UpdateCheckResult> ResolveAsync(
        IPortalUpdateClient portal,
        IUpdateFallback fallback,
        IActivationService activation,
        string currentVersion,
        IPlatformLog log,
        CancellationToken ct = default)
    {
        UpdateCheckResult portalResult;
        try
        {
            try { await activation.EnsureFreshSessionAsync(ct); }
            catch (Exception ex) { log.Warn("update", "session before check: " + ex.Message); }

            portalResult = await portal.CheckAsync(currentVersion, ct);
            if (portalResult.NeedsAuthRefresh)
            {
                var refreshed = await activation.RefreshSessionAsync(ct);
                if (refreshed)
                {
                    log.Info("update", "retrying portal update check after session refresh");
                    portalResult = await portal.CheckAsync(currentVersion, ct);
                }
                else if (portalResult.NeedsAuthRefresh)
                {
                    log.Info("update", "portal update check unauthorized — trying GitHub fleet feed");
                }
            }
        }
        catch (Exception ex)
        {
            log.Warn("update", "portal check failed: " + ex.Message);
            portalResult = new UpdateCheckResult { Message = "Portal update check failed." };
        }

        UpdateCheckResult? githubResult = null;
        try
        {
            githubResult = await fallback.TryCheckAsync(currentVersion, ct);
        }
        catch (Exception ex)
        {
            log.Warn("update", "GitHub fleet check failed: " + ex.Message);
        }

        return Merge(currentVersion, portalResult, githubResult, log);
    }

    public static UpdateCheckResult Merge(
        string currentVersion,
        UpdateCheckResult portal,
        UpdateCheckResult? github,
        IPlatformLog log)
    {
        var portalActionable = IsActionable(portal);
        var githubActionable = github is not null && IsActionable(github);

        if (portalActionable && githubActionable)
        {
            var portalVer = portal.LatestVersion ?? currentVersion;
            var ghVer = github!.LatestVersion ?? currentVersion;
            if (IsNewerVersion(ghVer, portalVer))
            {
                log.Info("update", $"GitHub {ghVer} newer than portal {portalVer} — fleet feed wins");
                return github!;
            }

            if (portal.IsMandatory && !github!.IsMandatory)
                return portal;

            log.Info("update", $"portal {portalVer} preferred over GitHub {ghVer}");
            return portal;
        }

        if (portalActionable)
            return portal;

        if (githubActionable)
        {
            log.Info("update", $"fleet GitHub update {github!.LatestVersion} (portal had no actionable release)");
            return github!;
        }

        // Neither actionable — preserve portal auth hint for interactive UI when relevant.
        if (portal.NeedsAuthRefresh)
            return portal;

        return github ?? portal;
    }

    private static bool IsActionable(UpdateCheckResult r) =>
        r.UpdateAvailable
        && !string.IsNullOrWhiteSpace(r.LatestVersion)
        && !string.IsNullOrWhiteSpace(r.DownloadUrl);

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
        var cleaned = new string(raw.Trim().TrimStart('v', 'V')
            .TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
        if (string.IsNullOrWhiteSpace(cleaned)) return new Version(0, 0, 0, 0);
        if (!cleaned.Contains('.')) cleaned += ".0";
        return Version.Parse(cleaned);
    }
}
