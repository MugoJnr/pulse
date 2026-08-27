using System.Windows;
using Microsoft.Win32;

namespace CpuTempWidget.Services;

/// <summary>
/// Subscribes to power / session / display SystemEvents and refreshes monitors without exiting.
/// </summary>
public sealed class PowerResilienceService : IDisposable
{
    private bool _started;
    private bool _disposed;

    public void Start()
    {
        if (_started) return;
        _started = true;

        try
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            DiagnosticLog.WritePower("PowerResilienceService started");
        }
        catch (Exception ex)
        {
            DiagnosticLog.WritePower("PowerResilienceService.Start failed", ex);
        }
    }

    /// <summary>
    /// Runs the same recovery path as real power events. Never exits the process.
    /// Safe for automated stress / regression tests.
    /// </summary>
    public static void SimulateTransition(string reason)
    {
        try
        {
            var includeAc = reason.Contains("Power", StringComparison.OrdinalIgnoreCase)
                            || reason.Contains("Battery", StringComparison.OrdinalIgnoreCase)
                            || reason.Contains("AC", StringComparison.OrdinalIgnoreCase)
                            || reason.Contains("Charger", StringComparison.OrdinalIgnoreCase);
            Handle(string.IsNullOrWhiteSpace(reason) ? "Simulate" : reason, includeAcLine: includeAc);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WritePower("SimulateTransition failed", ex, reason);
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        try
        {
            var reason = e.Mode switch
            {
                PowerModes.Resume => "PowerResume",
                PowerModes.Suspend => "PowerSuspend",
                PowerModes.StatusChange => "PowerStatusChange",
                _ => $"PowerMode:{e.Mode}"
            };
            Handle(reason, includeAcLine: true);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WritePower("OnPowerModeChanged failed", ex);
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        try
        {
            var reason = e.Reason switch
            {
                SessionSwitchReason.SessionLock => "SessionLock",
                SessionSwitchReason.SessionUnlock => "SessionUnlock",
                SessionSwitchReason.SessionLogon => "SessionLogon",
                SessionSwitchReason.SessionLogoff => "SessionLogoff",
                _ => $"SessionSwitch:{e.Reason}"
            };
            Handle(reason, includeAcLine: false);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WritePower("OnSessionSwitch failed", ex);
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        try
        {
            Handle("DisplaySettingsChanged", includeAcLine: false);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WritePower("OnDisplaySettingsChanged failed", ex);
        }
    }

    private static void Handle(string reason, bool includeAcLine)
    {
        try
        {
            var context = includeAcLine ? $"ac={ReadAcLine()}" : null;
            DiagnosticLog.WritePower(reason, context: context);

            void Work()
            {
                try
                {
                    SystemMonitor.NotifyPowerTransition(reason);
                    MainWindow.NotifyDisplayOrPowerChanged();
                }
                catch (Exception ex)
                {
                    DiagnosticLog.WritePower($"Handle work failed ({reason})", ex);
                }
            }

            var app = Application.Current;
            if (app?.Dispatcher is null)
            {
                Work();
                return;
            }

            if (app.Dispatcher.CheckAccess())
                Work();
            else
                app.Dispatcher.BeginInvoke(Work);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WritePower($"Handle failed ({reason})", ex);
        }
    }

    private static string ReadAcLine()
    {
        try
        {
            // 0 = offline, 1 = online, 255 = unknown — mirror SystemMonitor battery path.
            if (!GetSystemPowerStatus(out var status))
                return "unknown";
            return status.ACLineStatus switch
            {
                0 => "battery",
                1 => "ac",
                _ => "unknown"
            };
        }
        catch
        {
            return "unknown";
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            NativePowerHook.Detach();
            if (!_started) return;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            DiagnosticLog.WritePower("PowerResilienceService disposed");
        }
        catch { }
    }
}
