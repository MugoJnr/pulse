using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace CpuTempWidget;

public static class Branding
{
    public const string Company = "MugoByte Technologies";
    public const string ProductName = "Pulse";
    public const string ShortName = "Pulse";
    public const string Tagline = "Premium always-on-top system monitor";
    public const string Website = "https://mugobyte.com";
    /// <summary>
    /// Public installer feed (no source). Checked first so fleet PCs keep updating
    /// after the source repo is private. Override the whole feed with MBT_GITHUB_REPO.
    /// </summary>
    public const string GitHubReleasesRepo = "MugoJnr/pulse-releases";

    /// <summary>
    /// Source repository. Legacy update feed, checked after <see cref="GitHubReleasesRepo"/>.
    /// MBT_GITHUB_TOKEN / GH_TOKEN still authenticate this repo when it is private.
    /// </summary>
    public const string GitHubSourceRepo = "MugoJnr/pulse";

    /// <summary>Primary public update feed. Same as <see cref="GitHubReleasesRepo"/>.</summary>
    public const string GitHubRepo = GitHubReleasesRepo;

    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public static BitmapImage LoadPulseIcon(string fileName = "pulse-icon-transparent.png") =>
        LoadImage(fileName);

    public static BitmapImage LoadAppIcon() => LoadImage("pulse.ico");

    private static BitmapImage LoadImage(string fileName)
    {
        foreach (var uri in CandidateUris(fileName))
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = uri;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch { }
        }

        throw new FileNotFoundException($"Brand asset not found: {fileName}");
    }

    private static IEnumerable<Uri> CandidateUris(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly().GetName().Name ?? "Pulse";
        yield return new Uri($"pack://application:,,,/Assets/Brand/{fileName}", UriKind.Absolute);
        yield return new Uri($"pack://application:,,,/{assembly};component/Assets/Brand/{fileName}", UriKind.Absolute);

        var diskPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Brand", fileName);
        if (File.Exists(diskPath))
            yield return new Uri(diskPath, UriKind.Absolute);
    }
}
