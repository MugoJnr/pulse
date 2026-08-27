using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CpuTempWidget.Models;
using CpuTempWidget.Services;

namespace CpuTempWidget;

public partial class MainWindow : Window
{
    private static WeakReference<MainWindow>? _instanceRef;

    private const int GwlExStyle = -20;
    private const int WsExToolwindow = 0x00000080;
    private const int WsExAppwindow = 0x00040000;
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    private static readonly SolidColorBrush OkBrush = CreateBrush(0x86, 0xEF, 0xAC);
    private static readonly SolidColorBrush WarnBrush = CreateBrush(0xFB, 0xBF, 0x24);
    private static readonly SolidColorBrush CritBrush = CreateBrush(0xFB, 0x71, 0x75);
    private static readonly SolidColorBrush MuteBrush = CreateBrush(0x9C, 0xA3, 0xAF);
    private static readonly SolidColorBrush ChargeBrush = CreateBrush(0x7D, 0xD3, 0xFC);
    private static readonly SolidColorBrush FanOkBrush = CreateBrush(0x67, 0xE8, 0xF9);
    private static readonly SolidColorBrush FanWarnBrush = CreateBrush(0x38, 0xBD, 0xF8);
    private static readonly SolidColorBrush FanCritBrush = CreateBrush(0xF0, 0xAB, 0xFC);

    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _settingsSaveTimer;
    private readonly DispatcherTimer _trimTimer;
    private readonly DispatcherTimer _settingsAutohideTimer;
    private readonly DispatcherTimer _contrastTimer;
    private readonly DispatcherTimer _menuAutohideTimer;
    private double _backdropLum = 0.25;
    private Color _plateColor = Color.FromArgb(0x48, 0x02, 0x06, 0x12);
    private Color _outlineColor = Colors.Black;
    private SolidColorBrush? _plateBrush;
    private SystemMonitor? _monitor;
    private AppSettings _settings = new();
    private bool _dragging;
    private bool _suppressSliderEvents;
    private bool _adjustOpen;
    private Point _pressPoint;
    private string _lastSignature = string.Empty;
    private AppTheme? _appliedTheme;
    private bool _allowClose;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    private static readonly IntPtr HwndTopmost = new(-1);

    public MainWindow()
    {
        InitializeComponent();
        _instanceRef = new WeakReference<MainWindow>(this);

        ShowActivated = false;
        Visibility = Visibility.Visible;

        WirePulseMenu();
        PulseHost.RegisterOverlayPanel(() =>
        {
            Dispatcher.BeginInvoke(() => ToggleAdjustPanel(true));
        });

        _settings = SettingsService.Load();
        if (_settings.FontSize < 11 || _settings.FontSize > 32)
            _settings.FontSize = 13;
        if (_settings.Opacity < 0.35 || _settings.Opacity > 1.0)
            _settings.Opacity = 0.96;

        Closing += OnWindowClosing;

        ApplySettingsToUi();
        ApplyThemeUi();
        SettingsService.Save(_settings);

        _settingsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _settingsSaveTimer.Tick += (_, _) =>
        {
            _settingsSaveTimer.Stop();
            SettingsService.Save(_settings);
        };

        _trimTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _trimTimer.Tick += (_, _) => TrimMemory();

        _settingsAutohideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _settingsAutohideTimer.Tick += (_, _) =>
        {
            _settingsAutohideTimer.Stop();
            if (_adjustOpen && _settings.AutoHideSettings)
                ToggleAdjustPanel(false);
        };

        _contrastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(850) };
        _contrastTimer.Tick += (_, _) => TickAdaptiveContrast();

        _menuAutohideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _menuAutohideTimer.Tick += (_, _) =>
        {
            _menuAutohideTimer.Stop();
            if (PulseMenu.IsMouseOver) { _menuAutohideTimer.Start(); return; }
            MenuPopup.IsOpen = false;
        };
        PulseMenu.MouseEnter += (_, _) => { _menuAutohideTimer.Stop(); };
        PulseMenu.MouseLeave += (_, _) =>
        {
            if (!MenuPopup.IsOpen) return;
            _menuAutohideTimer.Interval = TimeSpan.FromSeconds(1.2);
            _menuAutohideTimer.Start();
        };

        SourceInitialized += (_, _) =>
        {
            ConfigureToolWindow();
            NativePowerHook.Attach(this);
        };

        Loaded += (_, _) =>
        {
            ApplyAlwaysOnTop();
            if (_settings.WidgetVisible)
            {
                Show();
                ActivateOverlay();
            }
            else
            {
                Hide();
            }

            _monitor ??= new SystemMonitor();
            RefreshReading();
            ApplyThemeUi();
            _trimTimer.Start();
            _plateBrush = new SolidColorBrush(_plateColor);
            HudPlate.Background = _plateBrush;
            TickAdaptiveContrast();
            _contrastTimer.Start();
            Dispatcher.BeginInvoke(TrimMemory, DispatcherPriority.ApplicationIdle);
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _monitor ??= new SystemMonitor();
            RefreshReading();
            if (App.ConsumeOpenShellSignal())
                PulseHost.ShowMain();
        };
        _timer.Start();

        SyncStartupPreference();

        LocationChanged += (_, _) =>
        {
            if (_dragging || IsLoaded)
                PersistPosition();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _settingsSaveTimer.Stop();
            _trimTimer.Stop();
            _settingsAutohideTimer.Stop();
            _contrastTimer.Stop();
            _menuAutohideTimer.Stop();
            PersistPosition();
            SettingsService.Save(_settings);
            _monitor?.Dispose();
        };
    }

    private static void TrimMemory()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
            EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
        }
        catch { }
    }

    private void ConfigureToolWindow()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var ex = GetWindowLong(hwnd, GwlExStyle);
        ex |= WsExToolwindow;
        ex &= ~WsExAppwindow;
        SetWindowLong(hwnd, GwlExStyle, ex);
        ActivateOverlay();
    }

    private void ActivateOverlay()
    {
        if (!_settings.AlwaysOnTop) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
    }

    private void ApplyAlwaysOnTop()
    {
        Topmost = _settings.AlwaysOnTop;
        if (_settings.AlwaysOnTop)
            ActivateOverlay();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        HideWidget();
    }

    /// <summary>Called by App.ExitPulse so Closing is not cancelled.</summary>
    public void AllowCloseForExit() => _allowClose = true;

    public void ShowWidget()
    {
        _settings.WidgetVisible = true;
        Visibility = Visibility.Visible;
        Show();
        WindowState = WindowState.Normal;
        ApplyAlwaysOnTop();
        SettingsService.Save(_settings);
    }

    public void HideWidget()
    {
        _settings.WidgetVisible = false;
        Hide();
        SettingsService.Save(_settings);
    }

    public void ToggleWidget()
    {
        if (IsVisible && Visibility == Visibility.Visible)
            HideWidget();
        else
            ShowWidget();
    }

    /// <summary>Power / display transition hook — clamp, persist, refresh.</summary>
    public void OnDisplayOrPowerChanged()
    {
        try
        {
            ClampToWorkingArea();
            PersistPosition();
            SettingsService.Save(_settings);
            _monitor ??= new SystemMonitor();
            RefreshReading();
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("MainWindow.OnDisplayOrPowerChanged", ex);
        }
    }

    /// <summary>Static entry used by PowerResilienceService (weak ref or MainWindow).</summary>
    public static void NotifyDisplayOrPowerChanged()
    {
        try
        {
            MainWindow? target = null;
            if (_instanceRef?.TryGetTarget(out var weak) == true)
                target = weak;
            else if (Application.Current?.MainWindow is MainWindow mw)
                target = mw;

            target?.OnDisplayOrPowerChanged();
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("MainWindow.NotifyDisplayOrPowerChanged", ex);
        }
    }

    private void WirePulseMenu()
    {
        PulseMenu.OpenRequested += (_, _) =>
        {
            CloseMenu();
            PulseHost.ShowMain();
        };
        PulseMenu.LaunchToggled += (_, _) =>
        {
            _settings.StartWithWindows = !_settings.StartWithWindows;
            var exe = SettingsService.ResolveLaunchExecutable(Environment.ProcessPath);
            if (!string.IsNullOrWhiteSpace(exe))
                SettingsService.ApplyStartup(_settings.StartWithWindows, exe);
            SettingsService.Save(_settings);
            PulseMenu.RefreshState(_settings.StartWithWindows, _settings.IsLocked);
            CloseMenu();
        };
        PulseMenu.LockToggled += (_, _) =>
        {
            _settings.IsLocked = !_settings.IsLocked;
            SettingsService.Save(_settings);
            PulseMenu.RefreshState(_settings.StartWithWindows, _settings.IsLocked);
            CloseMenu();
        };
        PulseMenu.RestartRequested += (_, _) => { CloseMenu(); SystemPowerService.RestartPulse(); };
        PulseMenu.CloseRequested += (_, _) => { CloseMenu(); App.ExitPulse(); };
        PulseMenu.ShutdownRequested += (_, _) =>
        {
            CloseMenu();
            SystemPowerService.ShutdownComputer();
        };
        PulseMenu.RestartPcRequested += (_, _) =>
        {
            CloseMenu();
            SystemPowerService.RestartComputer();
        };
        PulseMenu.AboutRequested += (_, _) =>
        {
            CloseMenu();
            new AboutWindow { Owner = this }.ShowDialog();
        };
    }

    private void ApplyThemeUi()
    {
        var theme = ThemeService.CurrentTheme;
        if (_appliedTheme == theme && IsLoaded) return;
        _appliedTheme = theme;

        var p = ThemeService.Palette;
        PulseMenu.ApplyTheme();
        PulseMenu.RefreshState(_settings.StartWithWindows, _settings.IsLocked);

        AdjustPanel.Background = p.PanelBackground;
        AdjustPanel.BorderBrush = p.FlyoutBorder;
        AdjustPanel.BorderThickness = new Thickness(1, 1, 1, 1);
        AdjustHint.Foreground = p.MutedBrush;
        SizeLabel.Foreground = p.TextBrush;
        OpacityLabel.Foreground = p.TextBrush;
        SizeValueText.Foreground = p.MutedBrush;
        OpacityValueText.Foreground = p.MutedBrush;
        AutoHideCheckBox.Foreground = p.TextBrush;
        AdaptiveCheckBox.Foreground = p.TextBrush;
        LayoutLabel.Foreground = p.TextBrush;
        LayoutCombo.Foreground = p.TextBrush;
        LayoutCombo.Background = p.PanelBackground;
        UpdateAdjustHint();
    }

    private void Border_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (MenuPopup.IsOpen)
        {
            CloseMenu();
            e.Handled = true;
            return;
        }

        if (_adjustOpen)
            ToggleAdjustPanel(false);

        ApplyThemeUi();
        PulseMenu.RefreshState(_settings.StartWithWindows, _settings.IsLocked);
        MenuPopup.IsOpen = true;
        _menuAutohideTimer.Interval = TimeSpan.FromSeconds(4);
        _menuAutohideTimer.Stop();
        _menuAutohideTimer.Start();
        e.Handled = true;
    }

    private void CloseMenu()
    {
        _menuAutohideTimer.Stop();
        MenuPopup.IsOpen = false;
    }

    private void ApplySettingsToUi()
    {
        Left = _settings.Left;
        Top = _settings.Top;
        ClampToWorkingArea();
        RootBorder.Opacity = _settings.Opacity;
        ApplyScale(_settings.FontSize);
        SyncSlidersFromSettings();
        PulseMenu.RefreshState(_settings.StartWithWindows, _settings.IsLocked);
        AutoHideCheckBox.IsChecked = _settings.AutoHideSettings;
        AdaptiveCheckBox.IsChecked = _settings.AdaptiveContrast;
        SelectLayoutCombo(_settings.OverlayLayout);
        ApplyOverlayLayout();
        UpdateAdjustHint();
    }

    private void ClampToWorkingArea()
    {
        var area = GetNearestWorkArea();
        if (area.Width < 80 || area.Height < 40) return;
        if (Left > area.Right - 80) Left = area.Right - 160;
        if (Top > area.Bottom - 40) Top = area.Bottom - 80;
        if (Left < area.Left) Left = area.Left + 12;
        if (Top < area.Top) Top = area.Top + 12;
    }

    /// <summary>Work area of the monitor the overlay currently sits on (multi-monitor safe).</summary>
    private Rect GetNearestWorkArea()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                var mon = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
                if (mon != IntPtr.Zero)
                {
                    var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(mon, ref info))
                    {
                        return new Rect(
                            info.rcWork.Left, info.rcWork.Top,
                            info.rcWork.Right - info.rcWork.Left,
                            info.rcWork.Bottom - info.rcWork.Top);
                    }
                }
            }
        }
        catch { }
        return SystemParameters.WorkArea;
    }

    private void SyncSlidersFromSettings()
    {
        _suppressSliderEvents = true;
        try
        {
            SizeSlider.Value = _settings.FontSize;
            OpacitySlider.Value = _settings.Opacity;
            SizeValueText.Text = _settings.FontSize.ToString("0.#", CultureInfo.InvariantCulture);
            OpacityValueText.Text = $"{Math.Round(_settings.Opacity * 100):0}%";
        }
        finally
        {
            _suppressSliderEvents = false;
        }
    }

    private void ApplyScale(double fontSize)
    {
        var scale = Math.Clamp(fontSize / 13.0, 0.85, 2.5);
        ContentRoot.LayoutTransform = new ScaleTransform(scale, scale);
    }

    private void SelectLayoutCombo(string layout)
    {
        _suppressSliderEvents = true;
        try
        {
            foreach (ComboBoxItem item in LayoutCombo.Items)
            {
                if (string.Equals(item.Tag as string, layout, StringComparison.OrdinalIgnoreCase))
                {
                    LayoutCombo.SelectedItem = item;
                    return;
                }
            }
            LayoutCombo.SelectedIndex = 0;
        }
        finally
        {
            _suppressSliderEvents = false;
        }
    }

    private void LayoutCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSliderEvents || !IsLoaded) return;
        if (LayoutCombo.SelectedItem is not ComboBoxItem item) return;
        var tag = item.Tag as string ?? "Strip";
        _settings.OverlayLayout = tag;
        ApplyOverlayLayout();
        QueueSettingsSave();
        ResetSettingsAutohideTimer();
    }

    private void ApplyOverlayLayout()
    {
        var layout = (_settings.OverlayLayout ?? "Strip").Trim();
        ContentRoot.RowDefinitions.Clear();
        ContentRoot.ColumnDefinitions.Clear();

        CpuValue.FontSize = 12;
        TempValue.FontSize = 12;
        RamValue.FontSize = 12;
        BatteryValue.FontSize = 12;
        ChargeWatts.FontSize = 11;
        HudPlate.Padding = new Thickness(14, 8, 14, 8);
        HudPlate.CornerRadius = new CornerRadius(18);

        switch (layout)
        {
            case "Quad":
                ApplyQuadLayout();
                break;
            case "Stack":
                ApplyStackLayout();
                break;
            case "Focus":
                ApplyFocusLayout();
                break;
            default:
                ApplyStripLayout();
                break;
        }
    }

    private void ApplyStripLayout()
    {
        for (var i = 0; i < 6; i++)
            ContentRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ContentRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Place(CpuCell, 0, 0, new Thickness(0, 0, 16, 0), HorizontalAlignment.Left);
        Place(TempCell, 0, 1, new Thickness(0, 0, 16, 0), HorizontalAlignment.Left);
        Place(RamCell, 0, 2, new Thickness(0, 0, 16, 0), HorizontalAlignment.Left);
        Place(BatteryCell, 0, 3, new Thickness(0, 0, 8, 0), HorizontalAlignment.Left);
        Place(WattsCell, 0, 4, new Thickness(0, 0, 12, 0), HorizontalAlignment.Left);
        Place(FanCell, 0, 5, new Thickness(0, 0, 0, 0), HorizontalAlignment.Left);
        ChargeWatts.FontSize = 11;
    }

    private void ApplyQuadLayout()
    {
        ContentRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ContentRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ContentRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ContentRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ContentRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ContentRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        HudPlate.Padding = new Thickness(14, 10, 14, 10);
        Place(CpuCell, 0, 0, new Thickness(0, 0, 12, 4), HorizontalAlignment.Left);
        Place(TempCell, 0, 1, new Thickness(0, 0, 0, 4), HorizontalAlignment.Left);
        Place(WattsCell, 1, 0, new Thickness(0, 2, 0, 2), HorizontalAlignment.Center, columnSpan: 2);
        Place(RamCell, 2, 0, new Thickness(0, 4, 12, 0), HorizontalAlignment.Left);
        Place(BatteryCell, 2, 1, new Thickness(0, 4, 0, 0), HorizontalAlignment.Left);
        Place(FanCell, 3, 0, new Thickness(0, 6, 0, 0), HorizontalAlignment.Center, columnSpan: 2);
        ChargeWatts.FontSize = 12;
    }

    private void ApplyStackLayout()
    {
        ContentRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < 6; i++)
            ContentRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        HudPlate.Padding = new Thickness(12, 10, 12, 10);
        HudPlate.CornerRadius = new CornerRadius(16);
        Place(CpuCell, 0, 0, new Thickness(0, 0, 0, 6), HorizontalAlignment.Left);
        Place(TempCell, 1, 0, new Thickness(0, 0, 0, 6), HorizontalAlignment.Left);
        Place(RamCell, 2, 0, new Thickness(0, 0, 0, 6), HorizontalAlignment.Left);
        Place(BatteryCell, 3, 0, new Thickness(0, 0, 0, 4), HorizontalAlignment.Left);
        Place(WattsCell, 4, 0, new Thickness(22, 0, 0, 4), HorizontalAlignment.Left);
        Place(FanCell, 5, 0, new Thickness(0, 2, 0, 0), HorizontalAlignment.Left);
    }

    private void ApplyFocusLayout()
    {
        ContentRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ContentRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ContentRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ContentRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ContentRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        HudPlate.Padding = new Thickness { Left = 16, Top = 10, Right = 16, Bottom = 10 };
        CpuValue.FontSize = 16;
        TempValue.FontSize = 16;
        Place(CpuCell, 0, 0, new Thickness(0, 0, 14, 6), HorizontalAlignment.Left);
        Place(TempCell, 0, 1, new Thickness(0, 0, 0, 6), HorizontalAlignment.Left);
        Place(RamCell, 1, 0, new Thickness(0, 0, 14, 0), HorizontalAlignment.Left);
        Place(BatteryCell, 1, 1, new Thickness(0, 0, 0, 0), HorizontalAlignment.Left);
        Place(WattsCell, 2, 0, new Thickness(0, 6, 0, 0), HorizontalAlignment.Center, columnSpan: 2);
        Place(FanCell, 2, 1, new Thickness(8, 6, 0, 0), HorizontalAlignment.Left);
    }

    private static void Place(
        FrameworkElement el, int row, int col, Thickness margin, HorizontalAlignment align,
        int columnSpan = 1)
    {
        Grid.SetRow(el, row);
        Grid.SetColumn(el, col);
        Grid.SetColumnSpan(el, columnSpan);
        Grid.SetRowSpan(el, 1);
        el.Margin = margin;
        el.HorizontalAlignment = align;
        el.VerticalAlignment = VerticalAlignment.Center;
    }

    private void SyncStartupPreference()
    {
        var exe = SettingsService.ResolveLaunchExecutable(Environment.ProcessPath);
        if (string.IsNullOrWhiteSpace(exe)) return;

        SettingsService.ApplyStartup(_settings.StartWithWindows, exe);
        PulseMenu.RefreshState(_settings.StartWithWindows, _settings.IsLocked);
    }

    private void RefreshReading()
    {
        if (_monitor is null) return;

        try
        {
            var reading = _monitor.Read();
            var cpu = (int)Math.Round(reading.CpuPercent);
            var ram = (int)Math.Round(reading.RamPercent);
            var tempText = reading.TemperatureC is float t ? $"{Math.Round(t):0}°" : "--°";

            string batteryText;
            var showCharge = false;
            double? watts = reading.OnAcPower ? reading.ChargeWatts : null;
            if (!reading.BatteryPresent || reading.BatteryPercent is null)
            {
                batteryText = reading.OnAcPower ? "AC" : "--";
                showCharge = reading.OnAcPower;
            }
            else
            {
                var bat = (int)Math.Round(reading.BatteryPercent.Value);
                batteryText = $"{bat}%";
                showCharge = reading.IsCharging || reading.OnAcPower;
            }

            var fanRpm = reading.FanRpm;
            var wattsText = watts is double ww ? FormatWatts(ww) : "";
            var signature =
                $"{cpu}|{tempText}|{ram}|{batteryText}|{showCharge}|{wattsText}|{reading.TemperatureC:0}|{reading.BatteryPercent:0}|{fanRpm}";
            if (string.Equals(signature, _lastSignature, StringComparison.Ordinal))
                return;

            _lastSignature = signature;

            CpuValue.Text = $"{cpu}%";
            TempValue.Text = tempText;
            RamValue.Text = $"{ram}%";
            BatteryValue.Text = batteryText;
            UpdateWattsLabel(reading.OnAcPower, watts, wattsText);
            UpdateFanDisplay(fanRpm);
            ApplyMetricColors(reading, showCharge);
        }
        catch
        {
            CpuValue.Text = "--%";
            TempValue.Text = "--°";
            RamValue.Text = "--%";
            BatteryValue.Text = "--";
            ChargeWatts.Visibility = Visibility.Collapsed;
            WattsCell.Visibility = Visibility.Collapsed;
            UpdateFanDisplay(null);
            SetPair(CpuIcon, CpuValue, MuteBrush);
            SetPair(TempIcon, TempValue, MuteBrush);
            SetPair(RamIcon, RamValue, MuteBrush);
            SetPair(BatteryIcon, BatteryValue, MuteBrush);
            ChargeWatts.Foreground = MuteBrush;
            _lastSignature = string.Empty;
        }
    }

    private void UpdateWattsLabel(bool onAc, double? watts, string wattsText)
    {
        if (!onAc)
        {
            ChargeWatts.Visibility = Visibility.Collapsed;
            WattsCell.Visibility = Visibility.Collapsed;
            ChargeWatts.Text = "";
            return;
        }

        WattsCell.Visibility = Visibility.Visible;
        ChargeWatts.Visibility = Visibility.Visible;
        ChargeWatts.Text = wattsText.Length > 0 ? wattsText : "0W";
        var filling = watts is double w && w > 0.4;
        ChargeWatts.ToolTip = filling
            ? $"Battery charge rate · {ChargeWatts.Text}"
            : "Charger connected · pack holding";
        ChargeWatts.Foreground = filling ? ChargeBrush : MuteBrush;
        ChargeWatts.Opacity = filling ? 1 : 0.75;
    }

    private void UpdateFanDisplay(int? fanRpm)
    {
        if (fanRpm is > 0)
        {
            FanCell.Visibility = Visibility.Visible;
            FanValue.Text = $"{fanRpm:N0}";
            var brush = BrushForFanLevel(fanRpm.Value);
            FanIcon.Foreground = brush;
            FanValue.Foreground = brush;
        }
        else
        {
            FanCell.Visibility = Visibility.Collapsed;
        }
    }

    private static SolidColorBrush BrushForFanLevel(int rpm)
    {
        if (rpm >= 4500) return FanCritBrush;
        if (rpm >= 2800) return FanWarnBrush;
        return FanOkBrush;
    }

    private void ApplyMetricColors(SystemReading reading, bool showCharge)
    {
        SetPair(CpuIcon, CpuValue, BrushForLevel(LevelFromCpu(reading.CpuPercent)));
        SetPair(TempIcon, TempValue,
            reading.TemperatureC is float temp ? BrushForLevel(LevelFromTemp(temp)) : MuteBrush);
        SetPair(RamIcon, RamValue, BrushForLevel(LevelFromRam(reading.RamPercent)));

        if (!reading.BatteryPresent || reading.BatteryPercent is null)
            SetPair(BatteryIcon, BatteryValue, MuteBrush);
        else
            SetPair(BatteryIcon, BatteryValue,
                BrushForLevel(LevelFromBattery(reading.BatteryPercent.Value, showCharge)));
    }

    private void TickAdaptiveContrast()
    {
        if (!_settings.AdaptiveContrast || _adjustOpen)
            return;

        var sample = BackdropSampler.TrySampleAround(this);
        if (sample is null) return;

        _backdropLum += (sample.Value.Luminance - _backdropLum) * 0.22;
        var bright = _backdropLum > 0.58;
        var targetPlate = bright
            ? Color.FromArgb(0x5A, 0x02, 0x06, 0x12)
            : Color.FromArgb(0x3A, 0x02, 0x06, 0x12);
        var targetOutline = bright ? Color.FromRgb(0x02, 0x06, 0x12) : Color.FromRgb(0x00, 0x00, 0x00);

        _plateColor = BackdropSampler.Lerp(_plateColor, targetPlate, 0.28);
        _outlineColor = BackdropSampler.Lerp(_outlineColor, targetOutline, 0.28);

        if (_plateBrush is null)
        {
            _plateBrush = new SolidColorBrush(_plateColor);
            HudPlate.Background = _plateBrush;
        }
        else
        {
            _plateBrush.Color = _plateColor;
        }

        var outline = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = bright ? 3.4 : 2.4,
            ShadowDepth = 0,
            Color = _outlineColor,
            Opacity = bright ? 0.92 : 0.78
        };
        outline.Freeze();
        ContentRoot.Effect = outline;
    }

    private static string FormatWatts(double watts)
    {
        if (watts >= 100)
            return $"{watts:0}W";
        if (watts >= 10)
            return $"{watts:0.#}W";
        return $"{watts:0.0}W";
    }

    private static void SetPair(TextBlock icon, TextBlock value, Brush brush)
    {
        icon.Foreground = brush;
        value.Foreground = brush;
    }

    private static SolidColorBrush BrushForLevel(MetricLevel level) => level switch
    {
        MetricLevel.Critical => CritBrush,
        MetricLevel.Warn => WarnBrush,
        _ => OkBrush
    };

    private static MetricLevel LevelFromCpu(float usage)
    {
        if (usage >= 90) return MetricLevel.Critical;
        if (usage >= 75) return MetricLevel.Warn;
        return MetricLevel.Ok;
    }

    private static MetricLevel LevelFromTemp(float celsius)
    {
        if (celsius >= 90) return MetricLevel.Critical;
        if (celsius >= 80) return MetricLevel.Warn;
        return MetricLevel.Ok;
    }

    private static MetricLevel LevelFromRam(float ramPercent)
    {
        if (ramPercent >= 92) return MetricLevel.Critical;
        if (ramPercent >= 82) return MetricLevel.Warn;
        return MetricLevel.Ok;
    }

    private static MetricLevel LevelFromBattery(float batteryPercent, bool charging)
    {
        if (charging) return batteryPercent <= 10 ? MetricLevel.Warn : MetricLevel.Ok;
        if (batteryPercent <= 12) return MetricLevel.Critical;
        if (batteryPercent <= 25) return MetricLevel.Warn;
        return MetricLevel.Ok;
    }

    private enum MetricLevel { Ok = 0, Warn = 1, Critical = 2 }

    private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressPoint = e.GetPosition(this);
        if (MenuPopup.IsOpen)
        {
            CloseMenu();
            e.Handled = true;
            return;
        }
        if (e.ClickCount == 2)
        {
            ToggleAdjustPanel();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is DependencyObject source && IsInsideAdjustControls(source)) return;

        if (_settings.IsLocked)
        {
            PulseHost.ShowMain();
            e.Handled = true;
            return;
        }

        _dragging = true;
        var start = PointToScreen(_pressPoint);
        try { DragMove(); }
        finally
        {
            _dragging = false;
            PersistPosition();
            SettingsService.Save(_settings);
            var end = PointToScreen(e.GetPosition(this));
            var dx = Math.Abs(end.X - start.X);
            var dy = Math.Abs(end.Y - start.Y);
            if (dx < 4 && dy < 4)
                PulseHost.ShowMain();
        }
    }

    private void Border_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _ = _pressPoint;

    private static bool IsInsideAdjustControls(DependencyObject source)
    {
        while (source is not null)
        {
            if (source is Slider) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void ToggleAdjustPanel(bool? forceOpen = null)
    {
        _adjustOpen = forceOpen ?? !_adjustOpen;
        AdjustPanel.Visibility = _adjustOpen ? Visibility.Visible : Visibility.Collapsed;
        if (_adjustOpen)
        {
            ApplyThemeUi();
            SyncSlidersFromSettings();
            AutoHideCheckBox.IsChecked = _settings.AutoHideSettings;
            ResetSettingsAutohideTimer();
        }
        else
        {
            _settingsAutohideTimer.Stop();
            SettingsService.Save(_settings);
        }
    }

    private void ResetSettingsAutohideTimer()
    {
        _settingsAutohideTimer.Stop();
        if (!_adjustOpen || !_settings.AutoHideSettings) return;
        _settingsAutohideTimer.Start();
    }

    private void AdjustPanel_MouseActivity(object sender, RoutedEventArgs e) => ResetSettingsAutohideTimer();

    /// <summary>Scrolling over sliders/combos must never change their value (accidental tweaks).</summary>
    private void AdjustPanel_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        DependencyObject? d = e.OriginalSource as DependencyObject;
        while (d is not null)
        {
            if (d is Slider or ComboBox)
            {
                e.Handled = true;
                break;
            }
            d = d is Visual || d is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        ResetSettingsAutohideTimer();
    }

    private void AutoHideCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _settings.AutoHideSettings = AutoHideCheckBox.IsChecked == true;
        UpdateAdjustHint();
        QueueSettingsSave();
        ResetSettingsAutohideTimer();
    }

    private void AdaptiveCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _settings.AdaptiveContrast = AdaptiveCheckBox.IsChecked == true;
        if (!_settings.AdaptiveContrast)
        {
            ContentRoot.Effect = null;
            if (_plateBrush is not null)
                _plateBrush.Color = Color.FromArgb(0x28, 0x02, 0x06, 0x12);
        }
        QueueSettingsSave();
    }

    private void UpdateAdjustHint()
    {
        AdjustHint.Text = _settings.AutoHideSettings
            ? "Panel auto-hides after 5s · double-click overlay to toggle"
            : "Auto-hide off · double-click overlay to close panel";
    }

    private void SizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvents || !IsLoaded) return;
        var size = Math.Round(e.NewValue, 1);
        _settings.FontSize = size;
        ApplyScale(size);
        SizeValueText.Text = size.ToString("0.#", CultureInfo.InvariantCulture);
        QueueSettingsSave();
        ResetSettingsAutohideTimer();
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvents || !IsLoaded) return;
        var opacity = Math.Clamp(e.NewValue, 0.35, 1.0);
        _settings.Opacity = opacity;
        RootBorder.Opacity = opacity;
        OpacityValueText.Text = $"{Math.Round(opacity * 100):0}%";
        QueueSettingsSave();
        ResetSettingsAutohideTimer();
    }

    private void QueueSettingsSave()
    {
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void PersistPosition()
    {
        _settings.Left = Left;
        _settings.Top = Top;
    }

    private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
