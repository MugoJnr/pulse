using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CpuTempWidget.Services;

namespace CpuTempWidget.Core;

/// <summary>Global Ctrl+Space command palette (fallback Ctrl+Shift+Space).</summary>
public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x5055; // 'PU'
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint VkSpace = 0x20;

    private HwndSource? _source;
    private bool _registered;
    private bool _usedFallback;

    public void Register(Window window)
    {
        var helper = new WindowInteropHelper(window);
        helper.EnsureHandle();
        _source = HwndSource.FromHwnd(helper.Handle);
        _source?.AddHook(WndProc);

        _registered = Native.RegisterHotKey(helper.Handle, HotkeyId, ModControl, VkSpace);
        if (!_registered)
        {
            _usedFallback = true;
            _registered = Native.RegisterHotKey(helper.Handle, HotkeyId, ModControl | ModShift, VkSpace);
        }

        try
        {
            FileAppend($"hotkey registered={_registered} fallback={_usedFallback}");
        }
        catch { }
    }

    public void Dispose()
    {
        if (_source is not null)
        {
            var hwnd = _source.Handle;
            if (_registered)
                Native.UnregisterHotKey(hwnd, HotkeyId);
            _source.RemoveHook(WndProc);
            _source = null;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int wmHotkey = 0x0312;
        if (msg == wmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                PulseHost.ShowMain();
                PulseHost.FocusSearch();
            });
        }
        return IntPtr.Zero;
    }

    private static void FileAppend(string line)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MugoByte", "Pulse", "startup.log");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, $"[{DateTime.Now:O}] {line}\n");
    }

    private static class Native
    {
        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
