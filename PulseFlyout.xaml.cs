using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CpuTempWidget.Services;

namespace CpuTempWidget;

public partial class PulseFlyout : UserControl
{
    private (Button Button, TextBlock Icon, TextBlock Label, bool Danger)[] _menuItems = [];

    public event EventHandler? OpenRequested;
    public event EventHandler? LaunchToggled;
    public event EventHandler? LockToggled;
    public event EventHandler? RestartRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler? ShutdownRequested;
    public event EventHandler? RestartPcRequested;
    public event EventHandler? AboutRequested;

    public PulseFlyout()
    {
        InitializeComponent();
        _menuItems =
        [
            (OpenButton, OpenIcon, OpenLabel, false),
            (LaunchButton, LaunchIcon, LaunchLabel, false),
            (LockButton, LockIcon, LockLabel, false),
            (RestartButton, RestartIcon, RestartLabel, false),
            (CloseButton, CloseIcon, CloseLabel, false),
            (RestartPcButton, RestartPcIcon, RestartPcLabel, false),
            (ShutdownButton, ShutdownIcon, ShutdownLabel, true),
            (AboutButton, AboutIcon, AboutLabel, false)
        ];

        VersionLine.Text = $"v{Branding.Version}";
        ApplyTheme();
    }

    public void ApplyTheme()
    {
        var p = ThemeService.Palette;
        Resources["MenuHoverBrush"] = p.ButtonHoverBrush;
        Resources["MenuPressedBrush"] = p.ButtonPressedBrush;

        RootBorder.Background = p.FlyoutBackground;
        RootBorder.BorderBrush = p.FlyoutBorder;
        ShadowBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 22,
            ShadowDepth = 0,
            Color = p.ShadowColor,
            Opacity = 0.55
        };

        ProductTitle.Foreground = p.TitleBrush;
        CompanyLine.Foreground = p.SubtitleBrush;
        VersionLine.Foreground = p.MutedBrush;
        TopSeparator.Background = p.SeparatorBrush;
        MidSeparator.Background = p.SeparatorBrush;
        BottomSeparator.Background = p.SeparatorBrush;

        PulseLogo.ApplyTheme();

        foreach (var (button, icon, label, danger) in _menuItems)
            StyleMenuItem(button, icon, label, p, danger);
    }

    public void RefreshState(bool launchEnabled, bool locked)
    {
        LaunchLabel.Text = launchEnabled ? "Launch at startup ✓" : "Launch at startup";
        LockLabel.Text = locked ? "Unlock position" : "Lock position";
    }

    private static void StyleMenuItem(Button button, TextBlock icon, TextBlock label, ThemePalette p, bool danger)
    {
        button.Foreground = p.TextBrush;
        button.Background = Brushes.Transparent;
        label.Foreground = p.TextBrush;
        icon.Foreground = danger ? p.DangerBrush : p.AccentBrush;
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, EventArgs.Empty);
    private void LaunchButton_Click(object sender, RoutedEventArgs e) => LaunchToggled?.Invoke(this, EventArgs.Empty);
    private void LockButton_Click(object sender, RoutedEventArgs e) => LockToggled?.Invoke(this, EventArgs.Empty);
    private void RestartButton_Click(object sender, RoutedEventArgs e) => RestartRequested?.Invoke(this, EventArgs.Empty);
    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void ShutdownButton_Click(object sender, RoutedEventArgs e) => ShutdownRequested?.Invoke(this, EventArgs.Empty);
    private void RestartPcButton_Click(object sender, RoutedEventArgs e) => RestartPcRequested?.Invoke(this, EventArgs.Empty);
    private void AboutButton_Click(object sender, RoutedEventArgs e) => AboutRequested?.Invoke(this, EventArgs.Empty);
}
