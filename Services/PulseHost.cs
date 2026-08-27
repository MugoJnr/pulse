using System.IO;
using System.Windows;

namespace CpuTempWidget.Services;

/// <summary>
/// Bridges overlay / flyout to the Pulse command-center main window.
/// </summary>
public static class PulseHost
{
    private static PulseMainWindow? _shell;
    private static Action? _openOverlayPanel;

    public static void RegisterOverlayPanel(Action openPanel) => _openOverlayPanel = openPanel;

    public static void ShowOverlayPanel() => _openOverlayPanel?.Invoke();

    public static void ShowMain(string? category = null)
    {
        void Open()
        {
            try
            {
                if (_shell is null || !_shell.IsLoaded)
                    _shell = new PulseMainWindow();

                _shell.Show();
                _shell.Visibility = Visibility.Visible;
                _shell.WindowState = WindowState.Normal;
                _shell.ShowInTaskbar = true;
                _shell.Activate();
                _shell.Focus();
                _shell.Topmost = true;
                _shell.Topmost = false;

                if (!string.IsNullOrWhiteSpace(category))
                    _shell.NavigateCategory(category);
                else
                    _shell.NavigateCategory("dashboard");
            }
            catch (Exception ex)
            {
                try
                {
                    var log = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "MugoByte", "Pulse", "error.log");
                    Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                    File.AppendAllText(log, $"[{DateTime.Now:O}] PulseHost.ShowMain: {ex}\n");
                }
                catch { }

                MessageBox.Show(
                    "Could not open Pulse command center.\n" + ex.Message,
                    Branding.ProductName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        var app = Application.Current;
        if (app?.Dispatcher is null)
        {
            Open();
            return;
        }

        if (app.Dispatcher.CheckAccess())
            Open();
        else
            app.Dispatcher.Invoke(Open);
    }

    public static void ShowCategory(string category) => ShowMain(category);

    public static void FocusSearch()
    {
        void Focus()
        {
            if (_shell is null || !_shell.IsLoaded) return;
            _shell.FocusSearchBox();
        }

        var app = Application.Current;
        if (app?.Dispatcher is null) { Focus(); return; }
        if (app.Dispatcher.CheckAccess()) Focus();
        else app.Dispatcher.Invoke(Focus);
    }

    public static void NotifyShellClosed() => _shell = null;
}
