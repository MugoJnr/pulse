using System.IO;
using System.Windows;

namespace CpuTempWidget.Services;

/// <summary>
/// WinForms NotifyIcon tray presence so Pulse can hide the overlay without exiting.
/// </summary>
public sealed class TrayService : IDisposable
{
    private System.Windows.Forms.NotifyIcon? _icon;
    private bool _disposed;

    public void Start()
    {
        if (_icon is not null) return;

        try
        {
            var icon = LoadIcon();
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Open Pulse", null, (_, _) => SafeUi(() => PulseHost.ShowMain()));
            menu.Items.Add("Show/Hide Widget", null, (_, _) => SafeUi(ToggleWidget));
            menu.Items.Add("Settings", null, (_, _) => SafeUi(OpenSettings));
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Exit Pulse", null, (_, _) => SafeUi(App.ExitPulse));

            _icon = new System.Windows.Forms.NotifyIcon
            {
                Icon = icon,
                Text = Branding.ProductName,
                Visible = true,
                ContextMenuStrip = menu
            };
            _icon.DoubleClick += (_, _) => SafeUi(() => PulseHost.ShowMain());
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("TrayService.Start failed", ex);
        }
    }

    private static void OpenSettings()
    {
        try
        {
            PulseHost.ShowOverlayPanel();
            if (Application.Current?.MainWindow is MainWindow { IsVisible: false })
                PulseHost.ShowMain("settings");
        }
        catch
        {
            PulseHost.ShowMain("settings");
        }
    }

    private static void ToggleWidget()
    {
        if (Application.Current?.MainWindow is MainWindow mw)
            mw.ToggleWidget();
    }

    private static void SafeUi(Action action)
    {
        try
        {
            var app = Application.Current;
            if (app?.Dispatcher is null)
            {
                action();
                return;
            }

            if (app.Dispatcher.CheckAccess())
                action();
            else
                app.Dispatcher.BeginInvoke(action);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("TrayService UI action failed", ex);
        }
    }

    private static System.Drawing.Icon LoadIcon()
    {
        foreach (var path in CandidateIconPaths())
        {
            try
            {
                if (File.Exists(path))
                    return new System.Drawing.Icon(path);
            }
            catch { }
        }

        return System.Drawing.SystemIcons.Application;
    }

    private static IEnumerable<string> CandidateIconPaths()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MugoByte", "Pulse", "pulse.ico");
        yield return Path.Combine(AppContext.BaseDirectory, "Assets", "Brand", "pulse.ico");
        yield return Path.Combine(AppContext.BaseDirectory, "pulse.ico");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_icon is null) return;
            _icon.Visible = false;
            _icon.Dispose();
            _icon = null;
        }
        catch { }
    }
}
