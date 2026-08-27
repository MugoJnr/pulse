using System.Windows;
using MugoByte.Platform;

namespace CpuTempWidget;

public partial class AccountGateWindow : Window
{
    private bool _createMode;

    public AccountGateWindow()
    {
        InitializeComponent();
        var activation = AppHost.Get<IActivationService>();
        HardwareIdBox.Text = activation.CurrentDevice.DeviceId;

        var opts = AppHost.Get<PlatformOptions>();
        if (opts.UseMock)
        {
            ModeHint.Text =
                "Demo portal mode is active. Sign In & Activate still follows the MBT POS process " +
                "(login → auto-claim). Credentials stay on this PC.";
            MockHintPanel.Visibility = Visibility.Visible;
        }

        try
        {
            var email = activation.CurrentSession?.User.Email;
            if (!string.IsNullOrWhiteSpace(email))
                EmailBox.Text = email;
        }
        catch { }
    }

    private void ToggleModeButton_Click(object sender, RoutedEventArgs e)
    {
        _createMode = !_createMode;
        TitleText.Text = _createMode ? "Create MugoByte account" : "Software Activation";
        ToggleModeButton.Content = _createMode ? "Have an account? Sign in" : "Create account";
        PrimaryButton.Content = _createMode ? "Create & Activate" : "Sign In & Activate";
        NameLabel.Visibility = NameBox.Visibility = _createMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        var email = EmailBox.Text.Trim();
        var password = PasswordBox.Password;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            SetStatus("Enter your MugoByte email and password.", error: true);
            return;
        }

        SetBusy(true);
        SetStatus(_createMode ? "Creating account…" : "Signing in…");
        try
        {
            var activation = AppHost.Get<IActivationService>();

            if (_createMode)
            {
                var auth = await activation.SignUpAsync(email, password, NameBox.Text.Trim());
                if (!auth.Ok)
                {
                    SetStatus(auth.Message, error: true);
                    return;
                }

                if (auth.VerificationRequired && auth.Session is null)
                {
                    SetStatus(auth.Message + " Then sign in to activate.", error: true);
                    _createMode = true;
                    ToggleModeButton_Click(sender, e);
                    return;
                }

                SetStatus("Activating this device from your account…");
                var key = OptionalLicenseKey();
                var act = await activation.ActivateCurrentDeviceAsync(key);
                if (!act.Ok)
                {
                    SetStatus(
                        act.Message + (string.IsNullOrWhiteSpace(key)
                            ? " You can paste a license key below instead."
                            : ""),
                        error: true);
                    return;
                }

                SetStatus(act.Message ?? "Device activated from your account.");
                DialogResult = true;
                Close();
                return;
            }

            // POS: Sign In & Activate → auto_claim_device_license
            PrimaryButton.Content = "Signing in…";
            SetStatus("Signing in and claiming a license seat…");
            var result = await activation.SignInAndActivateAsync(email, password, OptionalLicenseKey());
            if (!result.Ok)
            {
                SetStatus(result.Message, error: true);
                return;
            }

            SetStatus(result.Message ?? "Device activated from your account.");
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, error: true);
        }
        finally
        {
            PrimaryButton.Content = _createMode ? "Create & Activate" : "Sign In & Activate";
            SetBusy(false);
        }
    }

    private async void ActivateKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var key = LicenseKeyBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            SetStatus("Please enter the activation key.", error: true);
            LicenseKeyBox.Focus();
            return;
        }

        SetBusy(true);
        ActivateKeyButton.Content = "Activating…";
        SetStatus("Activating with cloud license key…");
        try
        {
            var activation = AppHost.Get<IActivationService>();
            if (!activation.IsSignedIn)
            {
                var email = EmailBox.Text.Trim();
                var password = PasswordBox.Password;
                if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
                {
                    SetStatus("Sign in above first, or enter email and password, then Activate License.", error: true);
                    return;
                }

                var auth = await activation.SignInAsync(email, password);
                if (!auth.Ok)
                {
                    SetStatus(auth.Message, error: true);
                    return;
                }
            }

            var act = await activation.ActivateCurrentDeviceAsync(key);
            if (!act.Ok)
            {
                SetStatus(act.Message, error: true);
                return;
            }

            SetStatus(act.Message ?? "License activated.");
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, error: true);
        }
        finally
        {
            ActivateKeyButton.Content = "Activate License";
            SetBusy(false);
        }
    }

    private void CopyHardwareId_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(HardwareIdBox.Text ?? "");
            SetStatus("Hardware ID copied to clipboard.");
        }
        catch
        {
            SetStatus("Could not copy Hardware ID.", error: true);
        }
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private string? OptionalLicenseKey()
    {
        var key = LicenseKeyBox.Text.Trim();
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    private void SetBusy(bool busy)
    {
        PrimaryButton.IsEnabled = !busy;
        ActivateKeyButton.IsEnabled = !busy;
        ToggleModeButton.IsEnabled = !busy;
        ExitButton.IsEnabled = !busy;
    }

    private void SetStatus(string message, bool error = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = error
            ? System.Windows.Media.Brushes.OrangeRed
            : message.Contains('…') || message.Contains("…") ||
              message.Contains("Signing", StringComparison.OrdinalIgnoreCase) ||
              message.Contains("Creating", StringComparison.OrdinalIgnoreCase) ||
              message.Contains("Activating", StringComparison.OrdinalIgnoreCase) ||
              message.Contains("claiming", StringComparison.OrdinalIgnoreCase)
                ? System.Windows.Media.Brushes.Orange
                : System.Windows.Media.Brushes.LightGreen;
    }
}
