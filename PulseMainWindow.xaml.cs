using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CpuTempWidget.Core;
using CpuTempWidget.Services;
using MugoByte.Platform;

namespace CpuTempWidget;

public partial class PulseMainWindow : Window
{
    private readonly DispatcherTimer _dashTimer;
    private readonly SystemMonitor _monitor = new();
    private string _category = "dashboard";
    private AppTheme? _theme;

    private static readonly (string Id, string Label, string Glyph)[] Categories =
        new (string, string, string)[]
        {
            ("dashboard", "Dashboard", "\uE80F"),
            ("account", "Account", "\uE77B"),
            ("alerts", "Alerts", "\uEA8F"),
            ("favorites", "Favorites", "\uE734"),
            ("recent", "Recent", "\uE81C"),
            ("diagnostics", "Diagnostics", "\uE9CE")
        }
        .Concat(ModuleRegistry.All.Select(m => (m.Id, m.Label, m.Glyph)))
        .ToArray();

    public PulseMainWindow()
    {
        InitializeComponent();
        BuildCategoryChips();
        ApplyTheme();
        FluentMaterial.TryApplyMica(this);
        NavigateCategory("dashboard");

        StateChanged += (_, _) =>
        {
            MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        };

        _dashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _dashTimer.Tick += (_, _) =>
        {
            RefreshStatusBar();
            try
            {
                var r = _monitor.Read();
                NotificationCenter.Evaluate(r);
            }
            catch { }

            if (_category == "dashboard" && SearchResultsPanel.Visibility != Visibility.Visible)
                RefreshDashboardMetrics();
        };
        _dashTimer.Start();
        RefreshStatusBar();

        Closed += (_, _) =>
        {
            _dashTimer.Stop();
            _monitor.Dispose();
            PulseHost.NotifyShellClosed();
        };

        Activated += (_, _) => ApplyTheme();
        PreviewKeyDown += Window_PreviewKeyDown;
        SearchBox.GotFocus += (_, _) => SearchPlaceholder.Visibility = Visibility.Collapsed;
        SearchBox.LostFocus += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(SearchBox.Text))
                SearchPlaceholder.Visibility = Visibility.Visible;
        };
        SourceInitialized += (_, _) => FluentMaterial.TryApplyMica(this);
    }

    public void FocusSearchBox()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
        SearchPlaceholder.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    public void NavigateCategory(string category)
    {
        _category = string.IsNullOrWhiteSpace(category) ? "dashboard" : category.ToLowerInvariant();
        SearchBox.Text = string.Empty;
        SearchPlaceholder.Visibility = Visibility.Visible;
        SearchResultsPanel.Visibility = Visibility.Collapsed;
        ContentScroll.Visibility = Visibility.Visible;
        HighlightCategoryChip();
        RenderCategory();
    }

    private void BuildCategoryChips()
    {
        CategoryBar.Children.Clear();
        foreach (var (id, label, glyph) in Categories)
        {
            var btn = new Button
            {
                Style = (Style)FindResource("ChipStyle"),
                Tag = id,
                Content = CreateIconLabel(glyph, label)
            };
            btn.Click += (_, _) => NavigateCategory(id);
            CategoryBar.Children.Add(btn);
        }
    }

    private static StackPanel CreateIconLabel(string glyph, string label, double glyphSize = 14)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = glyphSize,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E2E8F0"))
        });
        panel.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E2E8F0"))
        });
        return panel;
    }

    private void HighlightCategoryChip()
    {
        var p = ThemeService.Palette;
        foreach (Button btn in CategoryBar.Children)
        {
            var selected = string.Equals(btn.Tag as string, _category, StringComparison.OrdinalIgnoreCase);
            btn.Background = selected
                ? new SolidColorBrush(Color.FromArgb(0x55, 0x25, 0x63, 0xEB))
                : Brushes.Transparent;
            btn.Foreground = selected ? p.AccentBrush : p.TextBrush;
        }
    }

    private void ApplyTheme()
    {
        var theme = ThemeService.CurrentTheme;
        if (_theme == theme && IsLoaded) { HighlightCategoryChip(); return; }
        _theme = theme;
        var p = ThemeService.Palette;
        var dark = p.Theme == AppTheme.Dark;

        BgStop0.Color = dark ? Color.FromRgb(0x07, 0x0B, 0x14) : Color.FromRgb(0xF4, 0xF7, 0xFC);
        BgStop1.Color = dark ? Color.FromRgb(0x0B, 0x12, 0x20) : Color.FromRgb(0xEA, 0xF2, 0xFA);
        BgStop2.Color = dark ? Color.FromRgb(0x0F, 0x1B, 0x33) : Color.FromRgb(0xDE, 0xE8, 0xF6);

        Background = dark ? BrushRgb(0x07, 0x0B, 0x14) : BrushRgb(0xF4, 0xF7, 0xFC);
        RootChrome.BorderBrush = p.FlyoutBorder;
        TitleText.Foreground = p.TitleBrush;
        CompanyText.Foreground = p.AccentBrush;
        SearchChrome.Background = p.PanelBackground;
        SearchChrome.BorderBrush = dark
            ? new SolidColorBrush(Color.FromArgb(0xAA, 0x38, 0xBD, 0xF8))
            : p.AccentBrush;
        SearchBox.Foreground = p.TextBrush;
        SearchPlaceholder.Foreground = p.MutedBrush;
        SearchResultsPanel.Background = p.FlyoutBackground;
        SearchResultsPanel.BorderBrush = p.FlyoutBorder;
        SearchList.Foreground = p.TextBrush;
        StatusBar.BorderBrush = p.SeparatorBrush;
        StatusLeft.Foreground = p.MutedBrush;
        StatusRight.Foreground = p.MutedBrush;
        MinButton.Foreground = p.TextBrush;
        MaxButton.Foreground = p.TextBrush;
        CloseButton.Foreground = p.TextBrush;
        LogoMark.ApplyTheme();
        HighlightCategoryChip();
        if (SearchResultsPanel.Visibility != Visibility.Visible)
            RenderCategory();
    }

    private void RefreshStatusBar()
    {
        try
        {
            var r = _monitor.Read();
            var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
            StatusLeft.Text =
                $"{Environment.MachineName}  ·  CPU {r.CpuPercent:0}%  ·  RAM {r.RamPercent:0}%  ·  Up {uptime.Days}d {uptime.Hours}h {uptime.Minutes}m";

            var license = AppHost.Get<ILicenseGuard>().Evaluate();
            var plan = license.Claims?.LicenseType ?? "none";
            StatusRight.Text = license.State switch
            {
                LicenseState.GraceExpired => "Offline lock · reconnect",
                LicenseState.GraceWarning => $"Critical grace {license.GraceDaysRemaining}d · {plan}",
                LicenseState.Expiring => $"Expiring · {plan}",
                LicenseState.Active => $"{plan} · Ctrl+Space",
                _ => $"{license.State} · Ctrl+Space"
            };
        }
        catch { }
    }

    private void RenderAlerts()
    {
        var p = ThemeService.Palette;
        ContentHost.Children.Add(SectionTitle("Notifications", p));
        ContentHost.Children.Add(Hint("Persisted alerts with cooldown — duplicates are suppressed for 10 minutes.", p));

        var notes = NotificationCenter.All;
        if (notes.Count == 0)
        {
            ContentHost.Children.Add(Hint("No alerts yet. Pulse watches temperature, memory, storage, battery, network, and GPU load.", p));
            return;
        }

        foreach (var note in notes.Take(40))
        {
            var border = new Border
            {
                Background = p.PanelBackground,
                BorderBrush = note.Resolved ? p.FlyoutBorder : new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 0, 8)
            };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = note.Title + (note.Resolved ? "  ·  resolved" : ""),
                Foreground = p.TitleBrush,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13.5
            });
            stack.Children.Add(new TextBlock
            {
                Text = $"{note.Detail}  ·  {note.Utc.ToLocalTime():g}",
                Foreground = p.MutedBrush,
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            border.Child = stack;
            ContentHost.Children.Add(border);
        }
    }

    private void RenderAccount()
    {
        var p = ThemeService.Palette;
        var vm = AppHost.Get<AccountViewModel>();
        vm.Refresh();
        var opts = AppHost.Get<PlatformOptions>();

        ContentHost.Children.Add(SectionTitle("MugoByte Account", p));
        ContentHost.Children.Add(Hint(
            opts.UseMock
                ? "Mock portal mode — Sign In & Activate still follows the MBT POS process (login → auto-claim)."
                : "Same account onboarding as MBT POS: sign in → auto-claim seat → license key only if needed. Portal is the source of truth.",
            p));

        ContentHost.Children.Add(MakeInfoCard("Profile", new[]
        {
            vm.DisplayName,
            string.IsNullOrWhiteSpace(vm.Email) ? "No email" : vm.Email,
            vm.Version
        }, p));

        ContentHost.Children.Add(MakeInfoCard("License", new[]
        {
            vm.Plan,
            vm.LicenseState,
            vm.Device
        }, p));

        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(MakeAccountButton("Refresh license", p, async () =>
        {
            try
            {
                await AppHost.Get<IActivationService>().RefreshLicenseAsync();
                await AppHost.Get<IPlatformSync>().SynchronizeAsync();
            }
            catch { }
            NavigateCategory("account");
        }));
        actions.Children.Add(MakeAccountButton("Sign in / activate", p, () =>
        {
            new AccountGateWindow { Owner = this }.ShowDialog();
            NavigateCategory("account");
        }));
        actions.Children.Add(MakeAccountButton("Sign out", p, async () =>
        {
            try { await AppHost.Get<IActivationService>().SignOutAsync(); }
            catch { }
            new AccountGateWindow { Owner = this }.ShowDialog();
            NavigateCategory("account");
        }));
        actions.Children.Add(MakeAccountButton("Manage devices", p, () => AccountBootstrap.OpenPortal("devices")));
        actions.Children.Add(MakeAccountButton("Billing", p, () => AccountBootstrap.OpenPortal("billing")));
        actions.Children.Add(MakeAccountButton("Downloads", p, () => AccountBootstrap.OpenPortal("downloads")));
        actions.Children.Add(MakeAccountButton("Support", p, () => AccountBootstrap.OpenPortal("support")));
        actions.Children.Add(MakeAccountButton("Check for updates", p, UpdateService.CheckForUpdates));
        actions.Children.Add(MakeAccountButton(
            SettingsService.Load().SyncSettingsToPortal ? "Settings sync: On" : "Settings sync: Off (optional)",
            p,
            () =>
            {
                var s = SettingsService.Load();
                s.SyncSettingsToPortal = !s.SyncSettingsToPortal;
                SettingsService.Save(s);
                NavigateCategory("account");
            }));
        ContentHost.Children.Add(actions);
    }

    private Border MakeInfoCard(string title, IEnumerable<string> lines, ThemePalette p)
    {
        var border = new Border
        {
            Style = (Style)FindResource("CardStyle"),
            Margin = new Thickness(0, 0, 12, 12),
            MinWidth = 280,
            Padding = new Thickness(16)
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = p.MutedBrush,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8)
        });
        foreach (var line in lines)
        {
            stack.Children.Add(new TextBlock
            {
                Text = line,
                Foreground = p.TextBrush,
                FontSize = 13.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4)
            });
        }
        border.Child = stack;
        return border;
    }

    private Button MakeAccountButton(string label, ThemePalette p, Action onClick)
    {
        var btn = CreateAccountButtonChrome(label, p);
        btn.Click += (_, _) =>
        {
            try { onClick(); }
            catch { }
        };
        return btn;
    }

    private Button MakeAccountButton(string label, ThemePalette p, Func<Task> onClick)
    {
        var btn = CreateAccountButtonChrome(label, p);
        btn.Click += async (_, _) =>
        {
            btn.IsEnabled = false;
            try { await onClick(); }
            catch { }
            finally { btn.IsEnabled = true; }
        };
        return btn;
    }

    private static Button CreateAccountButtonChrome(string label, ThemePalette p) =>
        new()
        {
            Content = label,
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(14, 8, 14, 8),
            Cursor = Cursors.Hand,
            Background = p.ButtonHoverBrush,
            Foreground = p.TextBrush,
            BorderThickness = new Thickness(0),
            FontSize = 12.5
        };

    private void RenderCategory()
    {
        ContentHost.Children.Clear();
        switch (_category)
        {
            case "dashboard":
                RenderDashboard();
                break;
            case "account":
                RenderAccount();
                break;
            case "alerts":
                RenderAlerts();
                break;
            case "favorites":
                RenderActivityList(true);
                break;
            case "recent":
                RenderActivityList(false);
                break;
            case "diagnostics":
                RenderInfoPage("Diagnostics", DiagnosticsInfo.Lines(_monitor.Read()).Select(x => $"{x.Label}: {x.Value}"));
                break;
            case "hardware":
                RenderInfoPage("Hardware", BuildHardwareLines());
                RenderModuleActions("hardware");
                break;
            case "storage":
                RenderInfoPage("Storage", BuildStorageLines());
                RenderModuleActions("storage");
                break;
            case "battery":
                RenderInfoPage("Battery", BuildBatteryLines());
                RenderModuleActions("battery");
                break;
            case "applications":
                RenderModuleActions("applications");
                RenderProcessManager();
                break;
            case "performance":
                RenderModuleActions("performance");
                RenderStartupApps();
                break;
            default:
                RenderModuleActions(_category);
                break;
        }
    }

    private void RenderStartupApps()
    {
        var p = ThemeService.Palette;
        ContentHost.Children.Add(SectionTitle("Startup apps (current user)", p));
        ContentHost.Children.Add(Hint("HKCU Run entries. Removing only deletes the Run value — confirm before remove.", p));

        var box = new Border
        {
            Background = p.PanelBackground,
            BorderBrush = p.FlyoutBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 12)
        };
        var host = new StackPanel();
        foreach (var entry in StartupAppsService.ListUserRun().Take(40))
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.2, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });

            AddCell(row, 0, entry.Name, p.TextBrush);
            AddCell(row, 1, entry.Command, p.MutedBrush);
            AddCell(row, 2, entry.Enabled ? "Enabled" : "Disabled", p.MutedBrush);

            var name = entry.Name;
            var remove = MakeProcButton("Remove", p, () =>
            {
                var go = MessageBox.Show(
                    $"Remove startup entry “{name}”?",
                    Branding.ProductName,
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning);
                if (go != MessageBoxResult.OK) return;
                StartupAppsService.TryRemove(name);
                NavigateCategory("performance");
            }, danger: true);
            Grid.SetColumn(remove, 3);
            row.Children.Add(remove);
            host.Children.Add(row);
        }

        if (host.Children.Count == 0)
            host.Children.Add(new TextBlock { Text = "No user Run entries found.", Foreground = p.MutedBrush });

        box.Child = host;
        ContentHost.Children.Add(box);
    }

    private void RenderDashboard()
    {
        var p = ThemeService.Palette;
        var health = HealthScore.Compute(_monitor.Read());
        ContentHost.Children.Add(SectionTitle($"Health  {health.Score}%  ·  {health.Label}", p));
        ContentHost.Children.Add(Hint(health.Detail + "  ·  Ctrl+Space opens Pulse anywhere", p));

        var alerts = NotificationCenter.All.Where(n => !n.Resolved).Take(4).ToList();
        if (alerts.Count > 0)
        {
            ContentHost.Children.Add(SectionTitle($"Alerts  ({NotificationCenter.UnresolvedCount})", p));
            foreach (var note in alerts)
                ContentHost.Children.Add(Hint($"{note.Title} — {note.Detail}", p));
        }

        ContentHost.Children.Add(SectionTitle("Live status", p));
        ContentHost.Children.Add(BuildMetricCards());
        ContentHost.Children.Add(SectionTitle("Quick actions", p));
        ContentHost.Children.Add(BuildQuickActions());
        RefreshDashboardMetrics();
    }

    private IEnumerable<string> BuildBatteryLines()
    {
        var r = _monitor.Read();
        if (r.BatteryPresent && r.BatteryPercent is float b)
            yield return $"Charge: {b:0}%{(r.IsCharging ? " (charging)" : "")}";
        else
            yield return r.OnAcPower ? "AC power (no battery reported)" : "Battery unavailable";
        if (r.IsCharging && r.ChargeWatts is double w)
            yield return $"Charger: {w:0.#} W (live from battery firmware)";
        else if (r.IsCharging)
            yield return "Charger: waiting for battery rate";
        yield return $"Power plan tools available in Performance module";
    }

    private void RenderActivityList(bool favorites)
    {
        var p = ThemeService.Palette;
        ContentHost.Children.Add(SectionTitle(favorites ? "Favorites" : "Recent activity", p));
        ContentHost.Children.Add(Hint(favorites ? "Pin from search (right-click → Pin)" : "Commands you run appear here", p));

        var panel = new WrapPanel();
        if (favorites)
        {
            foreach (var fav in ActivityStore.GetFavorites())
            {
                var cmd = ModuleRegistry.AllCommands().FirstOrDefault(c => c.Id == fav.CommandId);
                if (cmd is null) continue;
                AddActionButton(panel, cmd);
            }
        }
        else
        {
            foreach (var rec in ActivityStore.GetRecent(24))
            {
                var cmd = ModuleRegistry.AllCommands().FirstOrDefault(c => c.Id == rec.CommandId);
                if (cmd is null) continue;
                AddActionButton(panel, cmd);
            }
        }

        if (panel.Children.Count == 0)
            ContentHost.Children.Add(new TextBlock { Text = "Nothing here yet.", Foreground = p.MutedBrush });
        else
            ContentHost.Children.Add(panel);
    }

    private void RenderModuleActions(string moduleId)
    {
        var p = ThemeService.Palette;
        var module = ModuleRegistry.Get(moduleId);
        var commands = module?.GetCommands().ToList() ?? PulseCatalog.CommandsFor(moduleId).ToList();
        if (commands.Count == 0) return;

        ContentHost.Children.Add(SectionTitle(module?.Label ?? moduleId, p));
        ContentHost.Children.Add(Hint("Runs through CommandDispatcher · safety prompts for destructive actions", p));

        var panel = new WrapPanel();
        foreach (var cmd in commands)
            AddActionButton(panel, cmd);
        ContentHost.Children.Add(panel);
    }

    private void AddActionButton(WrapPanel panel, IPulseCommand cmd)
    {
        var p = ThemeService.Palette;
        var btn = new Button
        {
            Style = (Style)FindResource("ActionStyle"),
            Background = p.PanelBackground,
            Foreground = p.TextBrush,
            Content = CreateIconLabel(cmd.Glyph, cmd.Title, 15),
            MinWidth = 240,
            ToolTip = cmd.Subtitle + (cmd.IsDestructive ? " · confirms first" : "")
        };
        btn.Click += (_, _) => CommandDispatcher.Execute(cmd);
        panel.Children.Add(btn);
    }

    private void RenderCatalogActions(string category) => RenderModuleActions(category);

    private void RenderProcessManager()
    {
        var p = ThemeService.Palette;
        var advanced = true;
        try { advanced = AppHost.Get<ILicenseGuard>().HasFeature("premium.advanced_process")
                         || AppHost.Get<ILicenseGuard>().Evaluate().State is LicenseState.Active
                             or LicenseState.Expiring or LicenseState.GraceWarning
                             or LicenseState.GraceExpired; }
        catch { }

        ContentHost.Children.Add(SectionTitle("Process manager", p));
        ContentHost.Children.Add(Hint(
            advanced
                ? "End · Kill tree · Suspend · Resume · Priority · Open location — destructive actions confirm"
                : "Basic end/kill available. Advanced process tools require an active Pulse license.",
            p));

        var filterBox = new TextBox
        {
            Margin = new Thickness(0, 0, 0, 12),
            Padding = new Thickness(12, 8, 12, 8),
            FontSize = 13,
            Background = p.PanelBackground,
            Foreground = p.TextBrush,
            BorderBrush = p.FlyoutBorder,
            Tag = "proc-filter"
        };
        var filterDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        filterDebounce.Tick += (_, _) =>
        {
            filterDebounce.Stop();
            RefreshProcessRows(hostPanel: null, filterBox, p, advanced);
        };
        filterBox.TextChanged += (_, _) =>
        {
            filterDebounce.Stop();
            filterDebounce.Start();
        };
        ContentHost.Children.Add(filterBox);

        var box = new Border
        {
            Background = p.PanelBackground,
            BorderBrush = p.FlyoutBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14),
            Tag = "proc-box"
        };
        var host = new StackPanel { Tag = "proc-host" };
        box.Child = host;
        ContentHost.Children.Add(box);
        filterBox.Tag = host;
        RefreshProcessRows(host, filterBox, p, advanced);
    }

    private void RefreshProcessRows(StackPanel? hostPanel, TextBox filterBox, ThemePalette p, bool advanced)
    {
        var host = hostPanel ?? filterBox.Tag as StackPanel;
        if (host is null) return;
        host.Children.Clear();

        var filter = filterBox.Text?.Trim() ?? "";

        // Header
        var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        DefineProcColumns(header, advanced);
        AddCell(header, 0, "Name", p.MutedBrush);
        AddCell(header, 1, "PID", p.MutedBrush);
        AddCell(header, 2, "CPU", p.MutedBrush);
        AddCell(header, 3, "RAM", p.MutedBrush);
        if (advanced) AddCell(header, 4, "Threads", p.MutedBrush);
        AddCell(header, advanced ? 5 : 4, "Actions", p.MutedBrush);
        host.Children.Add(header);

        try
        {
            var procs = Process.GetProcesses();
            try
            {
                var procInfos = procs
                    .Select(proc =>
                    {
                        try
                        {
                            return new
                            {
                                proc.ProcessName,
                                proc.Id,
                                Ws = proc.WorkingSet64,
                                Threads = proc.Threads.Count
                            };
                        }
                        catch { return null; }
                    })
                    .Where(x => x is not null)
                    .Where(x => string.IsNullOrWhiteSpace(filter)
                                || x!.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                || x.Id.ToString().Contains(filter))
                    .OrderByDescending(x => x!.Ws)
                    .Take(40)
                    .ToList();

                foreach (var proc in procInfos)
                {
                    if (proc is null) continue;
                    var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                    DefineProcColumns(row, advanced);

                    AddCell(row, 0, proc.ProcessName, p.TextBrush);
                    AddCell(row, 1, proc.Id.ToString(), p.MutedBrush);
                    AddCell(row, 2, "—", p.MutedBrush);
                    AddCell(row, 3, $"{proc.Ws / (1024.0 * 1024):0} MB", p.MutedBrush);
                    if (advanced) AddCell(row, 4, proc.Threads.ToString(), p.MutedBrush);

                    var actions = new WrapPanel { Orientation = Orientation.Horizontal };
                    var pid = proc.Id;
                    actions.Children.Add(MakeProcButton("End", p, () =>
                    {
                        ProcessProtection.TryEnd(pid, entireTree: false);
                        RefreshProcessRows(host, filterBox, p, advanced);
                    }));
                    actions.Children.Add(MakeProcButton("Kill tree", p, () =>
                    {
                        ProcessProtection.TryEnd(pid, entireTree: true);
                        RefreshProcessRows(host, filterBox, p, advanced);
                    }, danger: true));

                    if (advanced)
                    {
                        actions.Children.Add(MakeProcButton("Suspend", p, () =>
                        {
                            ProcessProtection.TrySuspend(pid);
                            RefreshProcessRows(host, filterBox, p, advanced);
                        }));
                        actions.Children.Add(MakeProcButton("Resume", p, () =>
                        {
                            ProcessProtection.TryResume(pid);
                            RefreshProcessRows(host, filterBox, p, advanced);
                        }));
                        actions.Children.Add(MakeProcButton("High", p, () =>
                            ProcessProtection.TrySetPriority(pid, ProcessPriorityClass.High)));
                        actions.Children.Add(MakeProcButton("Locate", p, () =>
                            ProcessProtection.TryOpenLocation(pid)));
                    }

                    Grid.SetColumn(actions, advanced ? 5 : 4);
                    row.Children.Add(actions);
                    host.Children.Add(row);
                }
            }
            finally
            {
                foreach (var p2 in procs)
                {
                    try { p2.Dispose(); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            host.Children.Add(new TextBlock
            {
                Text = "Could not list processes: " + ex.Message,
                Foreground = p.MutedBrush,
                Margin = new Thickness(0, 8, 0, 0)
            });
        }
    }

    private static void DefineProcColumns(Grid row, bool advanced)
    {
        row.ColumnDefinitions.Clear();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.0, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        if (advanced)
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3.2, GridUnitType.Star) });
    }

    private static Button MakeProcButton(string label, ThemePalette p, Action onClick, bool danger = false)
    {
        var btn = new Button
        {
            Content = label,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(4, 0, 0, 0),
            Cursor = Cursors.Hand,
            Background = danger ? new SolidColorBrush(Color.FromRgb(0x7F, 0x1D, 0x1D)) : p.ButtonHoverBrush,
            Foreground = danger ? Brushes.White : p.TextBrush,
            BorderThickness = new Thickness(0),
            FontSize = 11
        };
        btn.Click += (_, _) =>
        {
            try { onClick(); }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Branding.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        return btn;
    }

    private static void AddCell(Grid row, int col, string text, Brush brush)
    {
        var t = new TextBlock
        {
            Text = text,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(t, col);
        row.Children.Add(t);
    }

    private WrapPanel BuildMetricCards()
    {
        var panel = new WrapPanel { Name = "MetricCards" };
        panel.Children.Add(MakeCard("CpuCard", "\uE950", "CPU", "--"));
        panel.Children.Add(MakeCard("TempCard", "\uE9CA", "Temperature", "--"));
        panel.Children.Add(MakeCard("RamCard", "\uE8C8", "Memory", "--"));
        panel.Children.Add(MakeCard("GpuCard", "\uE7F4", "GPU", "--"));
        panel.Children.Add(MakeCard("StorageCard", "\uEDA2", "Storage", "--"));
        panel.Children.Add(MakeCard("BatteryCard", "\uE83F", "Battery", "--"));
        panel.Children.Add(MakeCard("NetCard", "\uE968", "Network", "--"));
        return panel;
    }

    private Border MakeCard(string name, string glyph, string label, string value)
    {
        var p = ThemeService.Palette;
        var border = new Border
        {
            Name = name,
            Style = (Style)FindResource("CardStyle"),
            Width = 200
        };
        border.Background = new LinearGradientBrush(
            Color.FromArgb(0xEE, 0x11, 0x18, 0x27),
            Color.FromArgb(0xCC, 0x0F, 0x1B, 0x33),
            90);
        if (p.Theme == AppTheme.Light)
        {
            border.Background = p.PanelBackground;
        }
        border.BorderBrush = p.FlyoutBorder;
        border.BorderThickness = new Thickness(1);
        border.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 22,
            ShadowDepth = 3,
            Opacity = 0.28,
            Color = Colors.Black
        };

        var stack = new StackPanel();
        var iconRow = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0x38, 0xBD, 0xF8)),
            Margin = new Thickness(0, 0, 0, 10),
            Child = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 16,
                Foreground = p.AccentBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        stack.Children.Add(iconRow);
        stack.Children.Add(new TextBlock { Text = label, Foreground = p.MutedBrush, FontSize = 11.5 });
        stack.Children.Add(new TextBlock
        {
            Name = name + "Value",
            Text = value,
            Foreground = p.TitleBrush,
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI")
        });
        border.Child = stack;
        border.Tag = name;
        return border;
    }

    private void RefreshDashboardMetrics()
    {
        try
        {
            var dash = AppHost.Get<DashboardViewModel>();
            dash.Refresh();
            SetCardValue("CpuCard", $"{dash.CpuPercent:0}%");
            SetCardValue("TempCard", dash.TempText);
            SetCardValue("RamCard", dash.RamText);
            SetCardValue("GpuCard", dash.GpuText);
            SetCardValue("StorageCard", dash.StorageText);
            SetCardValue("BatteryCard", dash.BatteryText);
            SetCardValue("NetCard", dash.NetworkText);
        }
        catch
        {
            try
            {
                var r = _monitor.Read();
                SetCardValue("CpuCard", $"{r.CpuPercent:0}%");
                SetCardValue("TempCard", r.TemperatureC is float t ? $"{t:0}°C" : "--");
                SetCardValue("RamCard", $"{r.RamUsedGb:0.0}/{r.RamTotalGb:0.0} GB");
                SetCardValue("GpuCard", FormatGpu(r));
                SetCardValue("StorageCard", $"{r.StoragePercent:0}% · {r.StorageLabel}");
                SetCardValue("BatteryCard", FormatBatteryCard(r));
                SetCardValue("NetCard", r.NetworkLabel);
            }
            catch { }
        }
    }

    private static string FormatBatteryCard(SystemReading r)
    {
        if (!r.BatteryPresent || r.BatteryPercent is not float b)
            return r.OnAcPower ? "AC" : "--";
        if (r.OnAcPower && r.ChargeWatts is double w)
            return $"{b:0}% · {w:0.#}W";
        return r.IsCharging ? $"{b:0}% +" : $"{b:0}%";
    }

    private static string FormatGpu(SystemReading r)
    {
        if (r.GpuLoadPercent is float load)
            return $"{load:0}%";
        if (!string.IsNullOrWhiteSpace(r.GpuName))
        {
            var shortName = r.GpuName.Length > 18 ? r.GpuName[..18] + "…" : r.GpuName;
            return shortName;
        }
        return "--";
    }

    private void SetCardValue(string cardName, string text)
    {
        foreach (var child in ContentHost.Children)
        {
            if (child is not WrapPanel wrap) continue;
            foreach (var item in wrap.Children)
            {
                if (item is not Border card) continue;
                if (!string.Equals(card.Tag as string, cardName, StringComparison.Ordinal)) continue;
                if (card.Child is StackPanel sp)
                {
                    foreach (var el in sp.Children)
                    {
                        if (el is TextBlock tb && tb.Name == cardName + "Value")
                        {
                            tb.Text = text;
                            return;
                        }
                    }
                }
            }
        }
    }

    private WrapPanel BuildQuickActions()
    {
        var panel = new WrapPanel();
        void Add(string label, string glyph, Action action)
        {
            var p = ThemeService.Palette;
            var btn = new Button
            {
                Style = (Style)FindResource("ActionStyle"),
                Background = p.PanelBackground,
                Foreground = p.TextBrush,
                Content = CreateIconLabel(glyph, label, 15),
                MinWidth = 200
            };
            btn.Click += (_, _) => { try { action(); } catch { } };
            panel.Children.Add(btn);
        }

        Add("Ultimate Performance", "\uE9D9", QuickActionsService.EnableUltimatePerformance);
        Add("High Performance", "\uE9D9", () => QuickActionsService.SetPowerPlan("High performance"));
        Add("Power Saver", "\uE83F", () => QuickActionsService.SetPowerPlan("Power saver"));
        Add("Dark Mode", "\uE708", QuickActionsService.SetDarkMode);
        Add("Light Mode", "\uE706", QuickActionsService.SetLightMode);
        Add("Clear Temp", "\uEA79", () =>
        {
            var cmd = PulseCatalog.Commands().FirstOrDefault(c => c.Title == "Clear Temp");
            if (cmd is not null) CommandDispatcher.Execute(cmd);
            else QuickActionsService.ClearTemp();
        });
        Add("Empty Recycle Bin", "\uE74D", () =>
        {
            var cmd = PulseCatalog.Commands().FirstOrDefault(c => c.Title.Contains("Empty Recycle"));
            if (cmd is not null) CommandDispatcher.Execute(cmd);
            else QuickActionsService.EmptyRecycleBin();
        });
        Add("Flush DNS", "\uE72C", QuickActionsService.FlushDns);
        Add("SFC Scan", "\uE90F", () =>
        {
            var cmd = PulseCatalog.Commands().FirstOrDefault(c => c.Title.Contains("SFC"));
            if (cmd is not null) CommandDispatcher.Execute(cmd);
            else QuickActionsService.RunSfc();
        });
        Add("Windows Security", "\uE72E", QuickActionsService.OpenWindowsSecurity);
        Add("Wi-Fi", "\uE701", QuickActionsService.OpenWifiSettings);
        Add("Bluetooth", "\uE702", QuickActionsService.OpenBluetoothSettings);
        Add("Restart Explorer", "\uE72C", QuickActionsService.RestartExplorer);
        Add("Task Manager", "\uE7F4", QuickActionsService.OpenTaskManager);
        Add("Device Manager", "\uE772", QuickActionsService.OpenDeviceManager);
        Add("Disk Cleanup", "\uEA79", QuickActionsService.OpenDiskCleanup);
        return panel;
    }

    private void RenderInfoPage(string title, IEnumerable<string> lines)
    {
        var p = ThemeService.Palette;
        ContentHost.Children.Add(SectionTitle(title, p));
        var box = new Border
        {
            Background = p.PanelBackground,
            BorderBrush = p.FlyoutBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(18),
            Margin = new Thickness(0, 0, 0, 14)
        };
        var stack = new StackPanel();
        foreach (var line in lines)
            stack.Children.Add(new TextBlock
            {
                Text = line,
                Foreground = p.TextBrush,
                Margin = new Thickness(0, 0, 0, 8),
                FontSize = 13.5
            });
        box.Child = stack;
        ContentHost.Children.Add(box);
    }

    private IEnumerable<string> BuildHardwareLines()
    {
        var r = _monitor.Read();
        yield return $"CPU load: {r.CpuPercent:0}%";
        yield return $"CPU temperature: {(r.TemperatureC is float t ? $"{t:0}°C" : "unavailable on this system")}";
        yield return $"Memory: {r.RamUsedGb:0.0} / {r.RamTotalGb:0.0} GB ({r.RamPercent:0}%)";
        yield return $"GPU: {r.GpuName ?? "unavailable"}{(r.GpuLoadPercent is float g ? $" · {g:0}% load" : "")}";
        yield return $"Network: {r.NetworkLabel}";
        yield return $"Fan: {(r.FanRpm is int f ? $"{f:N0} RPM" : "unavailable")}";
        yield return $"Machine: {Environment.MachineName}";
        yield return $"Logical processors: {Environment.ProcessorCount}";
        yield return $"OS: {Environment.OSVersion}";
        yield return $"64-bit OS: {Environment.Is64BitOperatingSystem}";
        yield return $"User: {Environment.UserName}";
    }

    private IEnumerable<string> BuildStorageLines()
    {
        foreach (var d in DriveInfo.GetDrives().Where(x => x.IsReady))
        {
            var used = d.TotalSize - d.AvailableFreeSpace;
            var pct = d.TotalSize > 0 ? 100.0 * used / d.TotalSize : 0;
            yield return $"{d.Name}  {d.DriveType}  {pct:0}% used  ({used / 1e9:0.0}/{d.TotalSize / 1e9:0.0} GB)";
        }
    }

    private static TextBlock SectionTitle(string text, ThemePalette p) =>
        new()
        {
            Text = text,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = p.TitleBrush,
            Margin = new Thickness(0, 6, 0, 8),
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI")
        };

    private static TextBlock Hint(string text, ThemePalette p) =>
        new()
        {
            Text = text,
            FontSize = 11.5,
            Foreground = p.MutedBrush,
            Margin = new Thickness(0, 0, 0, 12)
        };

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        var q = SearchBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(q))
        {
            SearchResultsPanel.Visibility = Visibility.Collapsed;
            ContentScroll.Visibility = Visibility.Visible;
            return;
        }

        var results = SearchService.Search(q);
        SearchList.ItemsSource = results;
        SearchResultsPanel.Visibility = Visibility.Visible;
        ContentScroll.Visibility = Visibility.Collapsed;
        if (results.Count > 0)
            SearchList.SelectedIndex = 0;
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && SearchList.Items.Count > 0)
        {
            SearchList.Focus();
            SearchList.SelectedIndex = Math.Max(0, SearchList.SelectedIndex);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            ExecuteSelectedSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SearchBox.Text = string.Empty;
            e.Handled = true;
        }
    }

    private void SearchList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ExecuteSelectedSearch();
    private void SearchList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ExecuteSelectedSearch(); e.Handled = true; }
    }

    private void SearchList_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void ExecuteSelectedSearch()
    {
        if (SearchList.SelectedItem is SearchItem item)
        {
            var q = SearchBox.Text?.Trim();
            try { item.Execute(); } catch { }
            if (!string.IsNullOrWhiteSpace(q))
                SearchHistory.Remember(q);
            SearchBox.Text = string.Empty;
        }
    }

    private void SearchList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (SearchList.SelectedItem is not SearchItem { Command: { } cmd }) return;
        var menu = new ContextMenu();
        menu.Items.Add(MakeMenu("Open", () => CommandDispatcher.Execute(cmd)));
        menu.Items.Add(MakeMenu(ActivityStore.IsFavorite(cmd.Id) ? "Unpin" : "Pin",
            () => ActivityStore.ToggleFavorite(cmd)));
        menu.Items.Add(MakeMenu("Copy path / id", () => Clipboard.SetText(cmd.Id + " · " + cmd.Subtitle)));
        if (cmd.RequiresElevation)
            menu.Items.Add(MakeMenu("Run as Administrator", () => CommandDispatcher.Execute(cmd)));
        menu.IsOpen = true;
    }

    private static MenuItem MakeMenu(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) MaxButton_Click(sender, e);
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void MinButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaxButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            MaxButton.Content = "\uE922";
        }
        else
        {
            WindowState = WindowState.Maximized;
            MaxButton.Content = "\uE923";
        }
    }
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        ShowInTaskbar = false;
    }

    private static SolidColorBrush BrushRgb(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
