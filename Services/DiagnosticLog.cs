using System.IO;
using System.Text;

namespace CpuTempWidget.Services;

/// <summary>
/// Best-effort diagnostic logging under %APPDATA%\MugoByte\Pulse\. Never throws.
/// </summary>
public static class DiagnosticLog
{
    private static readonly object Gate = new();

    public static string LogDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MugoByte", "Pulse");

    public static void Write(string fileName, string message, Exception? ex = null, string? context = null)
    {
        try
        {
            var dir = LogDirectory;
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            sb.Append('[').Append(DateTime.Now.ToString("O")).Append("] v")
              .Append(Branding.Version).Append(' ').Append(message);
            if (!string.IsNullOrWhiteSpace(context))
                sb.Append(" | ").Append(context);
            if (ex is not null)
                sb.AppendLine().Append(ex);
            sb.AppendLine();

            lock (Gate)
                File.AppendAllText(Path.Combine(dir, fileName), sb.ToString());
        }
        catch
        {
            // never throw
        }
    }

    public static void WriteError(string message, Exception? ex = null, string? context = null) =>
        Write("error.log", message, ex, context);

    public static void WritePower(string message, Exception? ex = null, string? context = null) =>
        Write("power.log", message, ex, context);

    public static void WriteTemp(string message, Exception? ex = null, string? context = null) =>
        Write("temp.log", message, ex, context);
}
