using System.Windows.Controls;
using System.Windows.Media;
using CpuTempWidget.Services;

namespace CpuTempWidget.Controls;

public partial class PulseMark : UserControl
{
    public PulseMark()
    {
        InitializeComponent();
        ApplyTheme();
    }

    public void ApplyTheme()
    {
        PulsePath.Stroke = ThemeService.Palette.AccentBrush;
    }
}
