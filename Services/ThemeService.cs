using System.Windows.Media;
using CpuTempWidget.Core;

namespace CpuTempWidget.Services;

public enum AppTheme { Light, Dark }

public sealed class ThemePalette
{
    public required AppTheme Theme { get; init; }
    public required Brush FlyoutBackground { get; init; }
    public required Brush FlyoutBorder { get; init; }
    public required Color ShadowColor { get; init; }
    public required Brush TitleBrush { get; init; }
    public required Brush SubtitleBrush { get; init; }
    public required Brush TextBrush { get; init; }
    public required Brush MutedBrush { get; init; }
    public required Brush AccentBrush { get; init; }
    public required Brush ButtonHoverBrush { get; init; }
    public required Brush ButtonPressedBrush { get; init; }
    public required Brush DangerBrush { get; init; }
    public required Brush SeparatorBrush { get; init; }
    public required Brush PanelBackground { get; init; }
    public required string PulseIconFile { get; init; }
}

public static class ThemeService
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static AppTheme CurrentTheme => ReadSystemTheme();

    public static ThemePalette Palette => CurrentTheme == AppTheme.Dark ? DarkPalette : LightPalette;

    private static readonly ThemePalette DarkPalette = new()
    {
        Theme = AppTheme.Dark,
        FlyoutBackground = BrushFromArgb(0xF0, 0x0F, 0x17, 0x2A),
        FlyoutBorder = BrushFromArgb(0xFF, 0x33, 0x41, 0x55),
        ShadowColor = Color.FromArgb(0x88, 0x00, 0x00, 0x00),
        TitleBrush = BrushFromRgb(0xF8, 0xFA, 0xFC),
        SubtitleBrush = BrushFromRgb(0x94, 0xA3, 0xB8),
        TextBrush = BrushFromRgb(0xE2, 0xE8, 0xF0),
        MutedBrush = BrushFromRgb(0x94, 0xA3, 0xB8),
        AccentBrush = BrushFromRgb(DesignTokens.AccentR, DesignTokens.AccentG, DesignTokens.AccentB),
        ButtonHoverBrush = BrushFromRgb(0x1E, 0x29, 0x3B),
        ButtonPressedBrush = BrushFromRgb(0x33, 0x41, 0x55),
        DangerBrush = BrushFromRgb(DesignTokens.DangerR, DesignTokens.DangerG, DesignTokens.DangerB),
        SeparatorBrush = BrushFromArgb(0x88, 0x33, 0x41, 0x55),
        PanelBackground = BrushFromArgb(0xCC, 0x11, 0x18, 0x27),
        PulseIconFile = "pulse-icon-transparent.png"
    };

    private static readonly ThemePalette LightPalette = new()
    {
        Theme = AppTheme.Light,
        FlyoutBackground = BrushFromArgb(0xF2, 0xFF, 0xFF, 0xFF),
        FlyoutBorder = BrushFromArgb(0xFF, 0xCB, 0xD5, 0xE1),
        ShadowColor = Color.FromArgb(0x55, 0x15, 0x23, 0x42),
        TitleBrush = BrushFromRgb(0x0F, 0x17, 0x2A),
        SubtitleBrush = BrushFromRgb(0x64, 0x74, 0x8B),
        TextBrush = BrushFromRgb(0x1E, 0x29, 0x3B),
        MutedBrush = BrushFromRgb(0x64, 0x74, 0x8B),
        AccentBrush = BrushFromRgb(DesignTokens.PrimaryR, DesignTokens.PrimaryG, DesignTokens.PrimaryB),
        ButtonHoverBrush = BrushFromRgb(0xE8, 0xF2, 0xFA),
        ButtonPressedBrush = BrushFromRgb(0xD6, 0xE8, 0xF8),
        DangerBrush = BrushFromRgb(DesignTokens.DangerR, DesignTokens.DangerG, DesignTokens.DangerB),
        SeparatorBrush = BrushFromArgb(0xAA, 0xCB, 0xD5, 0xE1),
        PanelBackground = BrushFromArgb(0xE6, 0xF8, 0xFA, 0xFC),
        PulseIconFile = "pulse-icon-transparent.png"
    };

    public static AppTheme ReadSystemTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(PersonalizeKey, false);
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int light) return light == 1 ? AppTheme.Light : AppTheme.Dark;
        }
        catch { }

        return AppTheme.Dark;
    }

    private static SolidColorBrush BrushFromRgb(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush BrushFromArgb(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
