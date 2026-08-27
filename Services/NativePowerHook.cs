using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MugoByte.Platform;

namespace CpuTempWidget.Services;

/// <summary>
/// Native WM_POWERBROADCAST / power-setting hooks on the MainWindow HWND.
/// Complements SystemEvents in <see cref="PowerResilienceService"/>.
/// </summary>
public static class NativePowerHook
{
    private const int WmPowerBroadcast = 0x0218;
    private const int PbtApmSuspend = 0x0004;
    private const int PbtApmResumeSuspend = 0x0007;
    private const int PbtApmResumeAutomatic = 0x0012;
    private const int PbtPowerSettingChange = 0x8013;

    private static readonly Guid GuidConsoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");
    private static readonly Guid GuidMonitorPowerOn = new("02731015-4510-4526-99e6-e5a17ebd1aea");

    private static HwndSource? _source;
    private static IntPtr _hConsoleDisplay;
    private static IntPtr _hMonitorPower;
    private static bool _hooked;

    public static void Attach(Window window)
    {
        if (_hooked || window is null) return;
        try
        {
            var helper = new WindowInteropHelper(window);
            helper.EnsureHandle();
            var hwnd = helper.Handle;
            if (hwnd == IntPtr.Zero) return;

            _source = HwndSource.FromHwnd(hwnd);
            if (_source is null) return;
            _source.AddHook(WndProc);

            var consoleDisplay = GuidConsoleDisplayState;
            var monitorPower = GuidMonitorPowerOn;
            _hConsoleDisplay = RegisterPowerSettingNotification(hwnd, ref consoleDisplay, 0);
            _hMonitorPower = RegisterPowerSettingNotification(hwnd, ref monitorPower, 0);
            _hooked = true;
            DiagnosticLog.WritePower($"NativePowerHook registered hwnd=0x{hwnd.ToInt64():X}");
            DiagnosticLog.WritePower("NativePowerHook attached");
        }
        catch (Exception ex)
        {
            DiagnosticLog.WritePower("NativePowerHook.Attach failed", ex);
        }
    }

    public static void Detach()
    {
        try
        {
            if (_source is not null)
            {
                _source.RemoveHook(WndProc);
                _source = null;
            }
            if (_hConsoleDisplay != IntPtr.Zero)
            {
                UnregisterPowerSettingNotification(_hConsoleDisplay);
                _hConsoleDisplay = IntPtr.Zero;
            }
            if (_hMonitorPower != IntPtr.Zero)
            {
                UnregisterPowerSettingNotification(_hMonitorPower);
                _hMonitorPower = IntPtr.Zero;
            }
            _hooked = false;
            DiagnosticLog.WritePower("NativePowerHook detached");
        }
        catch { }
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmPowerBroadcast) return IntPtr.Zero;
        try
        {
            var eventId = wParam.ToInt32();
            switch (eventId)
            {
                case PbtApmSuspend:
                    PowerResilienceService.SimulateTransition("PowerSuspend");
                    break;
                case PbtApmResumeSuspend:
                    OnResumeLike("PowerResumeSuspend");
                    break;
                case PbtApmResumeAutomatic:
                    OnResumeLike("PowerResumeAutomatic");
                    break;
                case PbtPowerSettingChange:
                    HandlePowerSetting(lParam);
                    break;
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.WritePower("NativePowerHook.WndProc failed", ex);
        }
        return IntPtr.Zero;
    }

    private static void HandlePowerSetting(IntPtr lParam)
    {
        if (lParam == IntPtr.Zero) return;
        var setting = Marshal.PtrToStructure<PowerBroadcastSetting>(lParam);
        if (setting.PowerSetting == GuidConsoleDisplayState || setting.PowerSetting == GuidMonitorPowerOn)
        {
            // Data is a DWORD: 0=off, 1=on, 2=dim (console display).
            var data = Marshal.ReadInt32(lParam, Marshal.SizeOf<PowerBroadcastSetting>());
            if (data == 1)
                OnResumeLike("DisplayOn");
            else if (data == 0)
                PowerResilienceService.SimulateTransition("DisplayOff");
        }
    }

    private static void OnResumeLike(string reason)
    {
        PowerResilienceService.SimulateTransition(reason);
        TrySoftSync(reason);
    }

    private static void TrySoftSync(string reason)
    {
        try
        {
            var sync = AppHost.Get<PlatformSyncHost>();
            _ = Task.Run(async () =>
            {
                try
                {
                    await sync.SynchronizeAsync().ConfigureAwait(false);
                    DiagnosticLog.WritePower($"soft sync after {reason}");
                }
                catch (Exception ex)
                {
                    DiagnosticLog.WritePower($"soft sync failed after {reason}", ex);
                }
            });
        }
        catch
        {
            // AppHost may not be ready — non-fatal.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerBroadcastSetting
    {
        public Guid PowerSetting;
        public int DataLength;
        // Data follows inline — read via Marshal.ReadInt32 with offset.
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
}
