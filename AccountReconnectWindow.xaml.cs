using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Threading;
using MugoByte.Platform;

namespace CpuTempWidget;

public partial class AccountReconnectWindow : Window
{
    private readonly DispatcherTimer _retry;
    private bool _busy;

    public AccountReconnectWindow(LicenseStatus status)
    {
        InitializeComponent();
        DetailText.Text = status.Message;
        Loaded += (_, _) => _ = TrySilentAsync();
        _retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _retry.Tick += async (_, _) => await TrySilentAsync();
        _retry.Start();
        NetworkChange.NetworkAvailabilityChanged += OnNetwork;
        NetworkChange.NetworkAddressChanged += OnAddress;
        Closed += (_, _) =>
        {
            _retry.Stop();
            NetworkChange.NetworkAvailabilityChanged -= OnNetwork;
            NetworkChange.NetworkAddressChanged -= OnAddress;
        };
    }

    private void OnNetwork(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable) return;
        Dispatcher.BeginInvoke(() => _ = TrySilentAsync());
    }

    private void OnAddress(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() => _ = TrySilentAsync());
    }

    private async Task TrySilentAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            HintText.Text = NetworkState.IsAvailable()
                ? "Network detected — refreshing your session automatically. You should not need to sign in again."
                : "Waiting for a network connection. Pulse will unlock automatically once you are online.";

            if (!NetworkState.IsAvailable())
                return;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var ok = await AppHost.Get<IActivationService>().TrySilentReconnectAsync(cts.Token);
            var status = AppHost.Get<ILicenseGuard>().Evaluate();
            DetailText.Text = status.Message;
            if (ok && status.AllowsCoreUse)
            {
                _retry.Stop();
                DialogResult = true;
                Close();
                return;
            }

            HintText.Text = NetworkState.IsAvailable()
                ? "Network is up, but Pulse could not confirm your license yet. If this keeps failing, use “Use another account” to sign in again."
                : "Waiting for a network connection. Pulse will unlock automatically once you are online.";
        }
        catch
        {
            DetailText.Text = "Could not reach the portal yet. Pulse will keep retrying.";
        }
        finally
        {
            _busy = false;
        }
    }

    private void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        var gate = new AccountGateWindow { Owner = Owner };
        if (gate.ShowDialog() == true)
        {
            var status = AppHost.Get<ILicenseGuard>().Evaluate();
            if (status.AllowsCoreUse)
            {
                DialogResult = true;
                Close();
                return;
            }
        }

        DetailText.Text = AppHost.Get<ILicenseGuard>().Evaluate().Message;
    }

    private void Portal_Click(object sender, RoutedEventArgs e)
    {
        var opts = AppHost.Get<PlatformOptions>();
        try
        {
            Process.Start(new ProcessStartInfo(opts.PortalAccountUrl) { UseShellExecute = true });
        }
        catch { }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
