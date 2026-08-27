using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using MugoByte.Platform;

namespace CpuTempWidget.Services;

/// <summary>
/// Stages the current installer's setup binary and relaunches it once for --self-update-test.
/// Guarded by env/marker so it never loops.
/// </summary>
public static class LocalUpdateE2E
{
    private static string MarkerPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MugoByte", "Pulse", "updates", "e2e-done.marker");

    public static bool AlreadyDone =>
        string.Equals(Environment.GetEnvironmentVariable("PULSE_UPDATE_E2E_DONE"), "1", StringComparison.OrdinalIgnoreCase)
        || File.Exists(MarkerPath);

    public static async Task RunAfterMainWindowAsync()
    {
        if (!UpdateE2EOptions.SelfUpdateTest || AlreadyDone)
            return;

        try
        {
            DiagnosticLog.Write("update-e2e.log", "self-update-test begin");
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            await File.WriteAllTextAsync(MarkerPath, DateTimeOffset.UtcNow.ToString("O"));
            Environment.SetEnvironmentVariable("PULSE_UPDATE_E2E_DONE", "1");

            var source = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            {
                DiagnosticLog.Write("update-e2e.log", "no process path");
                return;
            }

            var stageDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MugoByte", "Pulse", "updates");
            Directory.CreateDirectory(stageDir);
            var staged = Path.Combine(stageDir, $"Pulse-Setup-{Branding.Version}-e2e.exe");
            File.Copy(source, staged, overwrite: true);
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(staged))).ToLowerInvariant();
            await File.WriteAllTextAsync(staged + ".sha256", hash);

            DiagnosticLog.Write("update-e2e.log", $"staged {staged} sha256={hash}");

            Process.Start(new ProcessStartInfo(staged)
            {
                UseShellExecute = true,
                Arguments = "--dev --skip-account"
            });

            App.ExitPulse();
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("LocalUpdateE2E", ex);
        }
    }
}
