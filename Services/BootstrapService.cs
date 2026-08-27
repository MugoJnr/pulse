using System.Diagnostics;
using System.IO;

namespace CpuTempWidget.Services;

public static class BootstrapService
{
    public const string ProductFolder = "Pulse";
    public const string ExeFileName = "Pulse.exe";

    public static string InstallDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MugoByte",
            ProductFolder);

    public static string InstalledExecutable =>
        Path.Combine(InstallDirectory, ExeFileName);

    /// <summary>True when <paramref name="path"/> is the installed Pulse.exe location.</summary>
    public static bool IsInstalledPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return PathsEqual(path, InstalledExecutable);
    }

    /// <summary>
    /// True when source should replace target (missing, size/version/timestamp differ).
    /// Extracted for tests / E2E.
    /// </summary>
    public static bool ShouldRefreshInstall(string source, string target) => ShouldRefresh(source, target);

    /// <summary>Compare file versions; returns &gt;0 if a is newer than b.</summary>
    public static int CompareFileVersions(string pathA, string pathB)
    {
        try
        {
            var a = FileVersionInfo.GetVersionInfo(pathA).FileVersion ?? "0";
            var b = FileVersionInfo.GetVersionInfo(pathB).FileVersion ?? "0";
            var va = NormalizeVersion(a);
            var vb = NormalizeVersion(b);
            return va.CompareTo(vb);
        }
        catch
        {
            return 0;
        }
    }

    private static Version NormalizeVersion(string raw)
    {
        var cleaned = new string(raw.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
        if (string.IsNullOrWhiteSpace(cleaned)) return new Version(0, 0, 0, 0);
        if (!cleaned.Contains('.')) cleaned += ".0";
        return Version.Parse(cleaned);
    }

    /// <summary>
    /// Copies this exe into %LocalAppData%\MugoByte\Pulse\Pulse.exe when needed,
    /// then launches the installed copy and returns false (caller should exit).
    /// Returns true when already running from the install path (continue startup).
    /// </summary>
    public static bool EnsureInstalled()
    {
        var current = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(current) || !File.Exists(current))
            return true;

        var target = InstalledExecutable;
        if (IsInstalledPath(current))
            return true;

        try
        {
            Directory.CreateDirectory(InstallDirectory);

            // Setup/package launch must always restart the installed copy.
            // Otherwise a hung first instance makes Pulse look like it "does not launch".
            KillInstalledPulse(target);

            if (ShouldRefresh(current, target))
            {
                CopyApplicationPayload(Path.GetDirectoryName(current)!, current);
                try
                {
                    File.AppendAllText(
                        Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "MugoByte", "Pulse", "startup.log"),
                        $"[{DateTime.Now:O}] installed/updated -> {target} ({new FileInfo(target).Length} bytes)\n");
                }
                catch { }
            }

            if (!File.Exists(target))
                return true;

            var args = string.Join(" ",
                Environment.GetCommandLineArgs()
                    .Skip(1)
                    .Where(a => !a.Equals("--dev", StringComparison.OrdinalIgnoreCase))
                    .Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

            var started = Process.Start(new ProcessStartInfo(target)
            {
                UseShellExecute = true,
                WorkingDirectory = InstallDirectory,
                Arguments = args
            });

            if (started is null)
                return true;

            try { SettingsService.ApplyStartup(SettingsService.IsStartupEnabled() || IsSetupPackage(current), target); }
            catch { }

            try { ShortcutService.EnsureInstallShortcuts(target); }
            catch { }

            // Always exit the Setup/publish package after handoff.
            // Child may exit quickly when Pulse is already running (single-instance signal) — that is OK.
            return false;
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "MugoByte", "Pulse", "startup.log"),
                    $"[{DateTime.Now:O}] bootstrap FAILED: {ex.Message}\n");
            }
            catch { }

            return true;
        }
    }

    public static void ForceStopOtherPulseProcesses() =>
        KillInstalledPulse(InstalledExecutable);

    private static void KillInstalledPulse(string target)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("Pulse"))
            {
                try
                {
                    if (p.Id == Environment.ProcessId)
                        continue;
                    var path = "";
                    try { path = p.MainModule?.FileName ?? ""; } catch { }
                    if (string.IsNullOrWhiteSpace(path) || PathsEqual(path, target))
                        p.Kill(entireProcessTree: true);
                }
                catch { }
            }
            Thread.Sleep(400);
        }
        catch { }
    }

    private static bool IsSetupPackage(string currentExe)
    {
        var name = Path.GetFileNameWithoutExtension(currentExe);
        return name.Contains("Setup", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Installer", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldRefresh(string source, string target)
    {
        if (!File.Exists(target)) return true;
        try
        {
            var src = new FileInfo(source);
            var dst = new FileInfo(target);
            if (src.Length != dst.Length) return true;
            if (src.LastWriteTimeUtc > dst.LastWriteTimeUtc.AddSeconds(2)) return true;

            var srcVer = FileVersionInfo.GetVersionInfo(source).FileVersion;
            var dstVer = FileVersionInfo.GetVersionInfo(target).FileVersion;
            if (!string.Equals(srcVer, dstVer, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch { return true; }

        return false;
    }

    private static void CopyApplicationPayload(string sourceDir, string currentExe)
    {
        // Single-file / setup: copy only the running payload as Pulse.exe
        if (!IsFrameworkDependentLayout(sourceDir) || IsSetupPackage(currentExe))
        {
            var dest = InstalledExecutable;
            var temp = dest + ".new";
            File.Copy(currentExe, temp, overwrite: true);
            if (File.Exists(dest))
            {
                try { File.Delete(dest); }
                catch
                {
                    // Replace in place if delete blocked
                    File.Copy(temp, dest, overwrite: true);
                    try { File.Delete(temp); } catch { }
                    return;
                }
            }
            File.Move(temp, dest, overwrite: true);
            return;
        }

        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var dest = PathsEqual(file, currentExe)
                ? InstalledExecutable
                : Path.Combine(InstallDirectory, relative);

            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrWhiteSpace(destDir))
                Directory.CreateDirectory(destDir);

            File.Copy(file, dest, overwrite: true);
        }
    }

    private static bool IsFrameworkDependentLayout(string sourceDir) =>
        File.Exists(Path.Combine(sourceDir, "Pulse.dll"));

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
