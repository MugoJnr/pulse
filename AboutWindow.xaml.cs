using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using CpuTempWidget.Services;

namespace CpuTempWidget;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        ApplyTheme();

        try
        {
            LogoMark.ApplyTheme();
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
                Branding.LoadAppIcon().UriSource,
                System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        }
        catch { }

        ProductTitle.Text = Branding.ProductName;
        VersionText.Text = $"Version {Branding.Version}";
        TaglineText.Text = Branding.Tagline;
        Title = $"About {Branding.ShortName}";
    }

    private void ApplyTheme()
    {
        var p = ThemeService.Palette;
        var dark = p.Theme == AppTheme.Dark;
        Background = dark
            ? new SolidColorBrush(Color.FromRgb(0x0B, 0x12, 0x20))
            : new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC));
        RootBorder.BorderBrush = p.FlyoutBorder;
        WebsiteButton.Background = dark
            ? new SolidColorBrush(Color.FromRgb(0x16, 0x21, 0x3A))
            : new SolidColorBrush(Color.FromRgb(0xE8, 0xF2, 0xFA));
        WebsiteButton.Foreground = dark
            ? new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8))
            : new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
    }

    private void WebsiteButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Branding.Website) { UseShellExecute = true });
        }
        catch { }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
