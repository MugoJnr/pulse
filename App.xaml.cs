using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CpuTempWidget.Core;
using CpuTempWidget.Services;
using MugoByte.Platform;

namespace CpuTempWidget;

public partial class App : Application
{
    private SingleInstanceService? _singleInstance;
    private HotkeyService? _hotkeys;
    private TrayService? _tray;
    private PowerResilienceService? _power;
    private static App? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instance = this;

        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MugoByte", "Pulse");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "startup.log"),
                $"[{DateTime.Now:O}] start args=[{string.Join(' ', e.Args)}] path={Environment.ProcessPath} elevated={AdminLauncher.IsElevated}\n");
        }
        catch { }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                var ex = args.ExceptionObject as Exception;
                DiagnosticLog.WriteError("AppDomain.UnhandledException", ex,
                    $"isTerminating={args.IsTerminating}");
            }
            catch { }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            try
            {
                DiagnosticLog.WriteError("TaskScheduler.UnobservedTaskException", args.Exception);
                args.SetObserved();
            }
            catch { }
        };

        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                DiagnosticLog.WriteError("DispatcherUnhandledException", args.Exception);
            }
            catch { }

            args.Handled = true;
        };

        var openShell = e.Args.Any(a =>
            a.Equals("--shell", StringComparison.OrdinalIgnoreCase)
            || a.Equals("--open", StringComparison.OrdinalIgnoreCase));

        // Install from Setup/publish into AppData BEFORE single-instance so Setup never "owns" the mutex.
        var skipBootstrap = string.Equals(Environment.GetEnvironmentVariable("PULSE_SKIP_BOOTSTRAP"), "1", StringComparison.OrdinalIgnoreCase)
                            || e.Args.Any(a => a.Equals("--dev", StringComparison.OrdinalIgnoreCase));
        if (!skipBootstrap && !BootstrapService.EnsureInstalled())
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MugoByte", "Pulse", "startup.log"),
                    $"[{DateTime.Now:O}] bootstrap relaunched child — exiting package process\n");
            }
            catch { }

            // Shutdown() before base.OnStartup() can hang WPF; hard-exit the setup/package process.
            Environment.Exit(0);
            return;
        }

        _singleInstance = new SingleInstanceService();
        if (!_singleInstance.IsFirstInstance)
        {
            SignalOpenShell();
            Thread.Sleep(1200);
            if (IsHeartbeatFresh())
            {
                try
                {
                    File.AppendAllText(
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MugoByte", "Pulse", "startup.log"),
                        $"[{DateTime.Now:O}] not first instance — signaling running Pulse\n");
                }
                catch { }
                Environment.Exit(0);
                return;
            }

            try
            {
                File.AppendAllText(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MugoByte", "Pulse", "startup.log"),
                    $"[{DateTime.Now:O}] first instance hung — taking over\n");
            }
            catch { }

            BootstrapService.ForceStopOtherPulseProcesses();
            _singleInstance.Dispose();
            _singleInstance = new SingleInstanceService();
            if (!_singleInstance.IsFirstInstance)
            {
                Environment.Exit(0);
                return;
            }
        }

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var skipAccount = e.Args.Any(a => a.Equals("--skip-account", StringComparison.OrdinalIgnoreCase));
        AppHost.Build(e.Args);

        var settings = SettingsService.Load();
        settings.StartWithWindows = settings.StartWithWindows || !settings.HasSeenWelcome;
        if (!settings.HasSeenWelcome)
        {
            var welcome = new WelcomeWindow();
            if (welcome.ShowDialog() == true)
                settings.StartWithWindows = welcome.StartWithWindows;
            settings.HasSeenWelcome = true;
            SettingsService.Save(settings);
        }

        try { RegisterApplicationRestart(null, 0); } catch { }

        if (!AccountBootstrap.EnsureReady(skipAccount, out var licenseStatus))
        {
            Shutdown();
            return;
        }

        if (licenseStatus.State == LicenseState.GraceWarning)
            NotificationCenter.Push("License", licenseStatus.Message);

        settings.HasCompletedAccountSetup = true;
        SettingsService.Save(settings);
        AccountBootstrap.StartBackgroundServices();

        var main = new MainWindow();
        MainWindow = main;
        main.Show();
        WriteHeartbeat();

        _tray = new TrayService();
        _tray.Start();
        _power = new PowerResilienceService();
        _power.Start();
        if (main.IsLoaded || main.IsVisible)
            NativePowerHook.Attach(main);

        _hotkeys = new HotkeyService();
        _hotkeys.Register(main);

        if (openShell || ConsumeOpenShellSignal())
        {
            Dispatcher.BeginInvoke(() => PulseHost.ShowMain(),
                DispatcherPriority.ApplicationIdle);
        }

        if (UpdateE2EOptions.SelfUpdateTest)
        {
            Dispatcher.BeginInvoke(async () =>
            {
                await Task.Delay(1500);
                await LocalUpdateE2E.RunAfterMainWindowAsync();
            }, DispatcherPriority.ApplicationIdle);
        }

        // Watch for second-instance open signals (Setup/shortcut while already running).
        var signalTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(800)
        };
        signalTimer.Tick += (_, _) =>
        {
            WriteHeartbeat();
            if (!ConsumeOpenShellSignal()) return;
            if (MainWindow is Window overlay)
            {
                overlay.Show();
                overlay.WindowState = WindowState.Normal;
                overlay.Activate();
            }
            PulseHost.ShowMain();
        };
        signalTimer.Start();
    }

    /// <summary>Dispose tray/power/monitors then shut down the process explicitly.</summary>
    public static void ExitPulse()
    {
        try
        {
            if (Current?.MainWindow is MainWindow mw)
                mw.AllowCloseForExit();
        }
        catch { }

        try { _instance?._tray?.Dispose(); } catch { }
        try { _instance?._power?.Dispose(); } catch { }
        try { NativePowerHook.Detach(); } catch { }
        try { SystemMonitor.ShutdownSampler(); } catch { }

        try
        {
            Current?.Shutdown();
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("ExitPulse.Shutdown failed", ex);
            try { Environment.Exit(0); } catch { }
        }
    }

    private static string SignalPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MugoByte", "Pulse", "open.signal");

    public static void SignalOpenShell()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SignalPath)!);
            File.WriteAllText(SignalPath, DateTime.UtcNow.ToString("O"));
        }
        catch { }
    }

    public static bool ConsumeOpenShellSignal()
    {
        try
        {
            if (!File.Exists(SignalPath)) return false;
            File.Delete(SignalPath);
            return true;
        }
        catch { return false; }
    }

    private static string HeartbeatPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MugoByte", "Pulse", "heartbeat");

    public static void WriteHeartbeat()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HeartbeatPath)!);
            File.WriteAllText(HeartbeatPath, DateTime.UtcNow.ToString("O"));
        }
        catch { }
    }

    private static bool IsHeartbeatFresh()
    {
        try
        {
            if (!File.Exists(HeartbeatPath)) return false;
            var text = File.ReadAllText(HeartbeatPath).Trim();
            if (!DateTimeOffset.TryParse(text, out var stamped))
                return File.GetLastWriteTimeUtc(HeartbeatPath) > DateTime.UtcNow.AddSeconds(-4);
            return DateTimeOffset.UtcNow - stamped.ToUniversalTime() < TimeSpan.FromSeconds(4);
        }
        catch
        {
            return false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { UnregisterApplicationRestart(); } catch { }
        try { _tray?.Dispose(); } catch { }
        try { _power?.Dispose(); } catch { }
        try { SystemMonitor.ShutdownSampler(); } catch { }
        _hotkeys?.Dispose();
        try { AppHost.Get<PlatformSyncHost>().Dispose(); } catch { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string? pwzCommandLine, int dwFlags);

    [DllImport("kernel32.dll")]
    private static extern int UnregisterApplicationRestart();
}
