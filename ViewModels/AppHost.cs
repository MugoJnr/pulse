using CommunityToolkit.Mvvm.ComponentModel;
using CpuTempWidget.Core;
using CpuTempWidget.Models;
using CpuTempWidget.Services;
using Microsoft.Extensions.DependencyInjection;
using MugoByte.Platform;

namespace CpuTempWidget;

/// <summary>Production composition root (DI).</summary>
public static class AppHost
{
    private static IServiceProvider? _provider;

    public static IServiceProvider Services =>
        _provider ?? throw new InvalidOperationException("AppHost not built. Call Build() during startup.");

    public static void Build(string[]? args = null)
    {
        UpdateE2EOptions.Parse(args);

        var settings = SettingsService.Load();
        var useMock = settings.UseMockAccount
                      || (args?.Any(a => a.Equals("--mock-account", StringComparison.OrdinalIgnoreCase)) ?? false)
                      || string.Equals(Environment.GetEnvironmentVariable("MBT_PLATFORM_MODE"), "mock", StringComparison.OrdinalIgnoreCase);

        var options = PlatformOptions.ForPulse(Branding.Version, useMock);

        var sc = new ServiceCollection();
        sc.AddMugoBytePlatform(options);
        sc.AddSingleton<SystemMonitor>();
        sc.AddSingleton<ISystemMonitor, SystemMonitorAdapter>();
        sc.AddSingleton<UpdateCenter>();
        sc.AddSingleton<IUpdateFallback, GitHubUpdateFallback>();
        sc.AddSingleton<DashboardViewModel>();
        sc.AddSingleton<ShellViewModel>();
        sc.AddSingleton<AccountViewModel>();
        _provider = sc.BuildServiceProvider();
    }

    public static T Get<T>() where T : notnull => Services.GetRequiredService<T>();
}

public interface ISystemMonitor
{
    SystemReading Read();
}

public sealed class SystemMonitorAdapter : ISystemMonitor
{
    private readonly SystemMonitor _inner;
    public SystemMonitorAdapter(SystemMonitor inner) => _inner = inner;
    public SystemReading Read() => _inner.Read();
}

public partial class DashboardViewModel : ObservableObject
{
    private readonly ISystemMonitor _monitor;

    public DashboardViewModel(ISystemMonitor monitor) => _monitor = monitor;

    [ObservableProperty] private int _score;
    [ObservableProperty] private string _healthLabel = "—";
    [ObservableProperty] private string _healthDetail = "";
    [ObservableProperty] private float _cpuPercent;
    [ObservableProperty] private float _ramPercent;
    [ObservableProperty] private string _ramText = "--";
    [ObservableProperty] private string _tempText = "--";
    [ObservableProperty] private string _storageText = "--";
    [ObservableProperty] private string _batteryText = "--";
    [ObservableProperty] private string _networkText = "--";
    [ObservableProperty] private string _gpuText = "--";

    public void Refresh()
    {
        var r = _monitor.Read();
        var h = HealthScore.Compute(r);
        Score = h.Score;
        HealthLabel = h.Label;
        HealthDetail = h.Detail;
        CpuPercent = r.CpuPercent;
        RamPercent = r.RamPercent;
        RamText = $"{r.RamUsedGb:0.0}/{r.RamTotalGb:0.0} GB";
        TempText = r.TemperatureC is float t ? $"{t:0}°C" : "--";
        StorageText = $"{r.StoragePercent:0}% · {r.StorageLabel}";
        BatteryText = FormatBattery(r);
        NetworkText = r.NetworkLabel;
        GpuText = r.GpuLoadPercent is float g
            ? $"{g:0}%"
            : (!string.IsNullOrWhiteSpace(r.GpuName)
                ? (r.GpuName.Length > 18 ? r.GpuName[..18] + "…" : r.GpuName)
                : "--");
        NotificationCenter.Evaluate(r);
    }

    private static string FormatBattery(SystemReading r)
    {
        if (!r.BatteryPresent || r.BatteryPercent is not float b)
            return r.OnAcPower ? "AC" : "--";
        if (r.OnAcPower && r.ChargeWatts is double w)
            return $"{b:0}% · {w:0.#}W";
        return r.IsCharging ? $"{b:0}% +" : $"{b:0}%";
    }
}

public partial class ShellViewModel : ObservableObject
{
    [ObservableProperty] private string _statusLeft = "";
    [ObservableProperty] private string _statusRight = "MugoByte · Ctrl+Space";
    [ObservableProperty] private string _selectedModule = "dashboard";
}

public partial class AccountViewModel : ObservableObject
{
    private readonly IActivationService _activation;
    private readonly ILicenseGuard _guard;
    private readonly PlatformOptions _options;

    public AccountViewModel(IActivationService activation, ILicenseGuard guard, PlatformOptions options)
    {
        _activation = activation;
        _guard = guard;
        _options = options;
        Refresh();
    }

    [ObservableProperty] private string _displayName = "Not signed in";
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _plan = "";
    [ObservableProperty] private string _device = "";
    [ObservableProperty] private string _licenseState = "";
    [ObservableProperty] private string _version = "";
    [ObservableProperty] private bool _isMock;

    public void Refresh()
    {
        var status = _guard.Evaluate();
        var user = _activation.CurrentSession?.User;
        DisplayName = user?.DisplayName is { Length: > 0 } n ? n : (user?.Email ?? "Not signed in");
        Email = user?.Email ?? "";
        Plan = status.Claims is null
            ? "No license"
            : $"{status.Claims.PlanDisplayName} ({status.Claims.LicenseType})";
        Device = $"{_activation.CurrentDevice.DisplayName} · {_activation.CurrentDevice.DeviceId}";
        LicenseState = $"{status.State}: {status.Message}";
        Version = $"Pulse {_options.AppVersion}";
        IsMock = _options.UseMock;
    }
}
