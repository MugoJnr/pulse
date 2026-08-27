using System.Windows;
using System.Windows.Media;
using CpuTempWidget.Services;

namespace CpuTempWidget;

public partial class WelcomeWindow : Window
{
    public bool StartWithWindows { get; private set; } = true;

    public WelcomeWindow()
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

        Title = Branding.ProductName;
    }

    private void ApplyTheme()
    {
        var p = ThemeService.Palette;
        Background = p.Theme == AppTheme.Dark
            ? new SolidColorBrush(Color.FromRgb(0x0B, 0x12, 0x20))
            : new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC));
        RootBorder.BorderBrush = p.FlyoutBorder;
        GetStartedButton.Background = p.Theme == AppTheme.Dark
            ? new SolidColorBrush(Color.FromRgb(0x1E, 0x81, 0xC1))
            : new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
    }

    private void GetStartedButton_Click(object sender, RoutedEventArgs e)
    {
        StartWithWindows = StartupCheckBox.IsChecked == true;
        DialogResult = true;
        Close();
    }
}
