using CpuTempWidget.Core;
using MugoByte.Platform;

namespace CpuTempWidget.Services;

public sealed record PulseAction(
    string Title,
    string Subtitle,
    string Glyph,
    string Category,
    Action Execute,
    params string[] Keywords);

/// <summary>
/// Action inventory. Modules/search use <see cref="CommandsFor"/> via CommandDispatcher.
/// </summary>
public static class PulseCatalog
{
    private static IReadOnlyList<PulseAction>? _all;
    private static IReadOnlyList<IPulseCommand>? _commands;

    public static IReadOnlyList<PulseAction> All => _all ??= Build();

    public static IEnumerable<PulseAction> ForCategory(string category) =>
        All.Where(a => string.Equals(a.Category, category, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<IPulseCommand> CommandsFor(string category) =>
        Commands().Where(c => string.Equals(c.ModuleId, category, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<IPulseCommand> Commands() => _commands ??= All.Select(ToCommand).ToList();

    private static IPulseCommand ToCommand(PulseAction a)
    {
        var id = $"{a.Category}.{Slug(a.Title)}";
        var kind = a.Subtitle.StartsWith("ms-settings", StringComparison.OrdinalIgnoreCase)
            ? SearchResultKind.Setting
            : a.Title.Contains("Control Panel", StringComparison.OrdinalIgnoreCase)
                ? SearchResultKind.ControlPanel
                : a.Category is "maintenance" or "performance"
                    ? SearchResultKind.QuickAction
                    : SearchResultKind.Command;

        var destructive =
            a.Title.Contains("Empty Recycle", StringComparison.OrdinalIgnoreCase)
            || a.Title.Contains("Prefetch", StringComparison.OrdinalIgnoreCase)
            || a.Title.Contains("Network reset", StringComparison.OrdinalIgnoreCase)
            || a.Title.Contains("SFC", StringComparison.OrdinalIgnoreCase)
            || a.Title.Contains("DISM", StringComparison.OrdinalIgnoreCase)
            || a.Title.Contains("Clear Temp", StringComparison.OrdinalIgnoreCase)
            || a.Title.Contains("thumbnail", StringComparison.OrdinalIgnoreCase);

        var elev =
            a.Title.Contains("SFC", StringComparison.OrdinalIgnoreCase)
            || a.Title.Contains("DISM", StringComparison.OrdinalIgnoreCase)
            || a.Title.Contains("Group Policy", StringComparison.OrdinalIgnoreCase)
            || a.Title.Contains("Registry", StringComparison.OrdinalIgnoreCase)
            || a.Title.Contains("Network reset", StringComparison.OrdinalIgnoreCase)
            || a.Title.Contains("Restore Point", StringComparison.OrdinalIgnoreCase);

        var exec = a.Execute;
        return new PulseCommand(id, a.Title, a.Subtitle, a.Glyph, a.Category, () => exec(),
            kind, destructive, elev, a.Keywords);
    }

    private static string Slug(string title)
    {
        var chars = title.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var s = new string(chars);
        while (s.Contains("--", StringComparison.Ordinal)) s = s.Replace("--", "-", StringComparison.Ordinal);
        return s.Trim('-');
    }

    private static List<PulseAction> Build()
    {
        var a = new List<PulseAction>();

        void Add(string title, string sub, string glyph, string cat, Action exec, params string[] keys) =>
            a.Add(new PulseAction(title, sub, glyph, cat, exec, keys));

        // —— Performance ——
        Add("Ultimate Performance", "Enable ultimate power plan", "\uE9D9", "performance",
            QuickActionsService.EnableUltimatePerformance, "ultimate", "max");
        Add("Gaming profile", "High performance + Game Mode", "\uE7FC", "performance",
            () => { QuickActionsService.SetPowerPlan("High performance"); AdminLauncher.Uri("ms-settings:gaming-gamemode"); }, "profile", "gaming");
        Add("Work profile", "Balanced + focus", "\uE8F1", "performance",
            () => { QuickActionsService.SetPowerPlan("Balanced"); AdminLauncher.Uri("ms-settings:quiethours"); }, "profile", "work");
        Add("Battery Saver profile", "Power saver plan", "\uE83F", "performance",
            () => { QuickActionsService.SetPowerPlan("Power saver"); AdminLauncher.Uri("ms-settings:batterysaver"); }, "profile", "battery");
        Add("Presentation profile", "Focus assist", "\uE8A1", "performance",
            () => AdminLauncher.Uri("ms-settings:quiethours"), "profile", "presentation");
        Add("Developer profile", "For developers settings", "\uE943", "performance",
            () => AdminLauncher.Uri("ms-settings:developers"), "profile", "developer");
        Add("High Performance", "Switch power plan", "\uE9D9", "performance",
            () => QuickActionsService.SetPowerPlan("High performance"), "performance mode");
        Add("Balanced", "Balanced power plan", "\uE9D9", "performance",
            () => QuickActionsService.SetPowerPlan("Balanced"));
        Add("Power Saver", "Power saver plan", "\uE83F", "performance",
            () => QuickActionsService.SetPowerPlan("Power saver"), "battery saver");
        Add("Task Manager", "Processes and performance", "\uE7F4", "performance",
            QuickActionsService.OpenTaskManager, "processes");
        Add("Resource Monitor", "Detailed resource usage", "\uE9D9", "performance",
            () => AdminLauncher.Shell("resmon"), "resmon");
        Add("Performance Monitor", "perfmon graphs", "\uE9D2", "performance",
            () => AdminLauncher.Shell("perfmon"), "perfmon");
        Add("Power options", "Classic power settings", "\uE83F", "performance",
            () => AdminLauncher.Shell("powercfg.cpl"), "powercfg");
        Add("Sleep settings", "Screen and sleep", "\uE708", "performance",
            () => AdminLauncher.Uri("ms-settings:powersleep"));
        Add("Battery saver", "Battery saver settings", "\uE83F", "performance",
            () => AdminLauncher.Uri("ms-settings:batterysaver"));
        Add("Startup apps", "Manage startup impact", "\uE7E8", "performance",
            () => AdminLauncher.Uri("ms-settings:startupapps"), "startup");
        Add("Graphics settings", "GPU preferences", "\uE7F4", "performance",
            () => AdminLauncher.Uri("ms-settings:display-advancedgraphics"), "gpu");

        // —— Hardware ——
        Add("Device Manager", "Drivers and devices", "\uE772", "hardware",
            QuickActionsService.OpenDeviceManager, "drivers");
        Add("System Information", "msinfo32", "\uE9CE", "hardware",
            () => AdminLauncher.Shell("msinfo32"), "msinfo");
        Add("System properties", "Computer name and hardware", "\uE7F4", "hardware",
            () => AdminLauncher.Shell("sysdm.cpl"));
        Add("DirectX Diagnostic", "dxdiag", "\uE7F4", "hardware",
            () => AdminLauncher.Shell("dxdiag"), "gpu", "directx");
        Add("BIOS / UEFI", "Firmware settings (reboot)", "\uE950", "hardware",
            () => AdminLauncher.Uri("ms-settings:recovery"), "uefi", "firmware");
        Add("Bluetooth devices", "Hardware Bluetooth", "\uE702", "hardware",
            QuickActionsService.OpenBluetoothSettings);
        Add("Sound devices", "Audio hardware", "\uE767", "hardware",
            () => AdminLauncher.Uri("ms-settings:sound"));
        Add("Display adapters", "Display settings", "\uE7F4", "hardware",
            () => AdminLauncher.Uri("ms-settings:display"));
        Add("Battery report", "Generate HTML battery report", "\uE83F", "hardware",
            QuickActionsService.BatteryReport, "cycle", "health");

        // —— Battery ——
        Add("Battery settings", "Power & battery", "\uE83F", "battery",
            () => AdminLauncher.Uri("ms-settings:batterysaver"));
        Add("Battery report", "HTML health report", "\uE83F", "battery",
            QuickActionsService.BatteryReport, "cycle");
        Add("Power Saver", "Switch plan", "\uE83F", "battery",
            () => QuickActionsService.SetPowerPlan("Power saver"));
        Add("Sleep settings", "Screen and sleep", "\uE708", "battery",
            () => AdminLauncher.Uri("ms-settings:powersleep"));

        // —— Network ——
        Add("Wi-Fi", "Wireless settings", "\uE701", "network",
            QuickActionsService.OpenWifiSettings, "wifi", "wireless");
        Add("Ethernet", "Wired network", "\uE839", "network",
            () => AdminLauncher.Uri("ms-settings:network-ethernet"));
        Add("Bluetooth", "Bluetooth & devices", "\uE702", "network",
            QuickActionsService.OpenBluetoothSettings, "blu", "bt");
        Add("VPN", "VPN connections", "\uE968", "network",
            () => AdminLauncher.Uri("ms-settings:network-vpn"));
        Add("Mobile hotspot", "Share connection", "\uE704", "network",
            () => AdminLauncher.Uri("ms-settings:network-mobilehotspot"), "hotspot");
        Add("Proxy", "Proxy settings", "\uE968", "network",
            () => AdminLauncher.Uri("ms-settings:network-proxy"));
        Add("Firewall", "Windows Defender Firewall", "\uE72E", "network",
            () => AdminLauncher.Shell("firewall.cpl"));
        Add("Flush DNS", "ipconfig /flushdns", "\uE968", "network",
            QuickActionsService.FlushDns, "dns");
        Add("Release / Renew IP", "ipconfig release & renew", "\uE968", "network",
            QuickActionsService.ReleaseRenewIp, "ip");
        Add("Network reset", "Reset network stack", "\uE72C", "network",
            QuickActionsService.ResetWinsock, "winsock", "adapter reset");
        Add("Internet diagnostics", "Network troubleshooter", "\uE90F", "network",
            () => AdminLauncher.Shell("msdt.exe", "-id NetworkDiagnosticsNetwork"));
        Add("Network connections", "ncpa.cpl adapters", "\uE968", "network",
            () => AdminLauncher.Shell("ncpa.cpl"), "adapters");
        Add("Network status", "Status overview", "\uE968", "network",
            () => AdminLauncher.Uri("ms-settings:network-status"));
        Add("Nearby sharing", "Share nearby", "\uE72D", "network",
            () => AdminLauncher.Uri("ms-settings:crossdevice"));
        Add("Advanced sharing", "Network discovery", "\uE8F2", "network",
            () => AdminLauncher.Uri("ms-settings:network-advancedsharing"));

        // —— Maintenance ——
        Add("Disk Cleanup", "cleanmgr", "\uEA79", "maintenance",
            QuickActionsService.OpenDiskCleanup, "cleanup");
        Add("Clear Temp", "User + Windows temp", "\uEA79", "maintenance",
            QuickActionsService.ClearTemp, "temp", "clear cache");
        Add("Clear Prefetch", "Delete prefetch files", "\uEA79", "maintenance",
            QuickActionsService.ClearPrefetch, "prefetch");
        Add("Clear thumbnail cache", "Rebuild Explorer thumbs", "\uE8B9", "maintenance",
            QuickActionsService.ClearThumbnailCache, "thumbnails");
        Add("Empty Recycle Bin", "Clear recycle bin", "\uE74D", "maintenance",
            QuickActionsService.EmptyRecycleBin, "recycle");
        Add("Clear clipboard", "Empty clipboard", "\uE8C8", "maintenance",
            QuickActionsService.ClearClipboard, "clipboard");
        Add("Storage Sense", "Automatic cleanup", "\uEDA2", "maintenance",
            QuickActionsService.OpenStorageSense);
        Add("Optimize drives", "Defrag / TRIM", "\uE9D9", "maintenance",
            QuickActionsService.OptimizeDrives, "defrag", "trim");
        Add("Check Disk", "chkdsk /scan", "\uEDA2", "maintenance",
            QuickActionsService.CheckDisk, "chkdsk");
        Add("SFC Scan", "System File Checker", "\uE90F", "maintenance",
            QuickActionsService.RunSfc, "sfc", "repair");
        Add("DISM Restore", "Repair Windows image", "\uE90F", "maintenance",
            QuickActionsService.RunDism, "dism", "repair windows");
        Add("Create Restore Point", "System restore checkpoint", "\uE81C", "maintenance",
            QuickActionsService.CreateRestorePoint, "restore");
        Add("Restart Explorer", "Refresh Windows shell", "\uE72C", "maintenance",
            QuickActionsService.RestartExplorer, "explorer");
        Add("Windows Update", "Check for updates", "\uE895", "maintenance",
            () => AdminLauncher.Uri("ms-settings:windowsupdate"), "update");
        Add("Recovery", "Reset / advanced startup", "\uE777", "maintenance",
            () => AdminLauncher.Uri("ms-settings:recovery"));
        Add("Reliability Monitor", "Stability history", "\uE9D2", "maintenance",
            () => AdminLauncher.Shell("perfmon", "/rel"));

        // —— Applications ——
        Add("Installed apps", "Apps & features", "\uE71D", "applications",
            () => AdminLauncher.Uri("ms-settings:appsfeatures"), "programs", "uninstall");
        Add("Startup apps", "Launch at login", "\uE7E8", "applications",
            () => AdminLauncher.Uri("ms-settings:startupapps"));
        Add("Default apps", "File associations", "\uE8A5", "applications",
            () => AdminLauncher.Uri("ms-settings:defaultapps"));
        Add("Optional features", "Windows optional features", "\uE74C", "applications",
            () => AdminLauncher.Uri("ms-settings:optionalfeatures"));
        Add("Programs and Features", "appwiz.cpl", "\uE71D", "applications",
            () => AdminLauncher.Shell("appwiz.cpl"));
        Add("Task Manager", "End / restart apps", "\uE7F4", "applications",
            QuickActionsService.OpenTaskManager);
        Add("Store apps", "Microsoft Store", "\uE7F8", "applications",
            () => AdminLauncher.Uri("ms-windows-store:"));

        // —— Security ——
        Add("Windows Security", "Defender hub", "\uE72E", "security",
            QuickActionsService.OpenWindowsSecurity, "defender");
        Add("Firewall", "Windows Firewall", "\uE72E", "security",
            () => AdminLauncher.Shell("firewall.cpl"));
        Add("BitLocker", "Device encryption", "\uE72E", "security",
            () => AdminLauncher.Uri("ms-settings:deviceencryption"), "encryption");
        Add("Credential Manager", "Saved passwords", "\uE72E", "security",
            () => AdminLauncher.Shell("control", "/name Microsoft.CredentialManager"), "passwords");
        Add("User Accounts", "netplwiz", "\uE77B", "security",
            () => AdminLauncher.Shell("netplwiz"), "uac", "users");
        Add("Windows Hello", "Sign-in options", "\uE1E0", "security",
            () => AdminLauncher.Uri("ms-settings:signinoptions"));
        Add("Privacy", "Privacy dashboard", "\uE72E", "security",
            () => AdminLauncher.Uri("ms-settings:privacy"));
        Add("Secure Boot / Recovery", "Recovery options", "\uE777", "security",
            () => AdminLauncher.Uri("ms-settings:recovery"));
        Add("Local Security Policy", "secpol.msc", "\uE72E", "security",
            () => AdminLauncher.Shell("secpol.msc"));
        Add("Create Restore Point", "Security checkpoint", "\uE81C", "security",
            QuickActionsService.CreateRestorePoint);

        // —— Storage ——
        Add("Storage settings", "Drive usage", "\uEDA2", "storage",
            () => AdminLauncher.Uri("ms-settings:storagesense"));
        Add("Disk Management", "Partitions", "\uEDA2", "storage",
            () => AdminLauncher.Shell("diskmgmt.msc"), "partitions");
        Add("Optimize drives", "Defrag / TRIM", "\uE9D9", "storage",
            QuickActionsService.OptimizeDrives);
        Add("Disk Cleanup", "Free space", "\uEA79", "storage",
            QuickActionsService.OpenDiskCleanup);
        Add("Check Disk", "Scan volume", "\uEDA2", "storage",
            QuickActionsService.CheckDisk);
        Add("This PC", "Explorer drives", "\uE8B7", "storage",
            () => AdminLauncher.Shell("explorer", "shell:MyComputerFolder"));
        Add("USB devices", "Connected devices", "\uE88E", "storage",
            () => AdminLauncher.Uri("ms-settings:connecteddevices"));

        // —— Developer ——
        Add("Windows Terminal", "Modern terminal", "\uE756", "developer",
            () => AdminLauncher.Shell("wt"), "terminal");
        Add("PowerShell", "Windows PowerShell", "\uE756", "developer",
            () => AdminLauncher.Shell("powershell"));
        Add("Command Prompt", "CMD", "\uE756", "developer",
            () => AdminLauncher.Shell("cmd"), "cmd");
        Add("Environment Variables", "System properties advanced", "\uE943", "developer",
            () => AdminLauncher.Shell("SystemPropertiesAdvanced.exe"), "env", "path");
        Add("Hosts file", "Edit hosts in Notepad", "\uE8A5", "developer",
            QuickActionsService.OpenHostsFile, "hosts");
        Add("Hosts folder", "drivers\\etc", "\uE8B7", "developer",
            () => AdminLauncher.Shell("explorer", @"C:\Windows\System32\drivers\etc"));
        Add("Services", "Running services", "\uE90F", "developer",
            () => AdminLauncher.Shell("services.msc"));
        Add("Registry Editor", "regedit", "\uE8F1", "developer",
            () => AdminLauncher.Shell("regedit"));
        Add("God Mode", "All Control Panel items", "\uE8FC", "developer",
            () => AdminLauncher.Shell("explorer", "shell:::{ED7BA470-8E54-465E-825C-99712043E01C}"));
        Add("WSL", "Windows Subsystem for Linux", "\uE756", "developer",
            () => AdminLauncher.Shell("wsl"), "linux");
        Add("Open ports", "netstat listening", "\uE968", "developer",
            () => AdminLauncher.CmdVisible("netstat -ano"), "ports");

        // —— Windows ——
        Add("Windows Settings", "ms-settings:home", "\uE713", "windows",
            () => AdminLauncher.Uri("ms-settings:"), "settings");
        Add("Control Panel", "Classic Control Panel", "\uE8FD", "windows",
            () => AdminLauncher.Shell("control"));
        Add("Task Manager", "taskmgr", "\uE7F4", "windows",
            QuickActionsService.OpenTaskManager);
        Add("Device Manager", "devmgmt.msc", "\uE772", "windows",
            QuickActionsService.OpenDeviceManager);
        Add("Disk Management", "diskmgmt.msc", "\uEDA2", "windows",
            () => AdminLauncher.Shell("diskmgmt.msc"));
        Add("Computer Management", "compmgmt.msc", "\uE8FC", "windows",
            () => AdminLauncher.Shell("compmgmt.msc"));
        Add("Services", "services.msc", "\uE90F", "windows",
            () => AdminLauncher.Shell("services.msc"));
        Add("Registry Editor", "regedit", "\uE8F1", "windows",
            () => AdminLauncher.Shell("regedit"));
        Add("Group Policy", "gpedit.msc", "\uE8F1", "windows",
            () => AdminLauncher.Shell("gpedit.msc"), "gpedit");
        Add("Performance Monitor", "perfmon", "\uE9D2", "windows",
            () => AdminLauncher.Shell("perfmon"));
        Add("Resource Monitor", "resmon", "\uE9D9", "windows",
            () => AdminLauncher.Shell("resmon"));
        Add("Task Scheduler", "taskschd.msc", "\uE916", "windows",
            () => AdminLauncher.Shell("taskschd.msc"));
        Add("System Information", "msinfo32", "\uE9CE", "windows",
            () => AdminLauncher.Shell("msinfo32"));
        Add("Event Viewer", "eventvwr.msc", "\uE8FD", "windows",
            () => AdminLauncher.Shell("eventvwr.msc"));
        Add("Reliability Monitor", "Stability", "\uE9D2", "windows",
            () => AdminLauncher.Shell("perfmon", "/rel"));
        Add("Windows Update", "Updates", "\uE895", "windows",
            () => AdminLauncher.Uri("ms-settings:windowsupdate"));
        Add("Optional Features", "Features on demand", "\uE74C", "windows",
            () => AdminLauncher.Uri("ms-settings:optionalfeatures"));
        Add("Programs and Features", "appwiz.cpl", "\uE71D", "windows",
            () => AdminLauncher.Shell("appwiz.cpl"));
        Add("Character Map", "charmap", "\uE8D2", "windows",
            () => AdminLauncher.Shell("charmap"));
        Add("Snipping Tool", "Screen capture", "\uE7C8", "windows",
            () => AdminLauncher.Shell("snippingtool"));
        Add("Remote Desktop", "mstsc", "\uE8AF", "windows",
            () => AdminLauncher.Shell("mstsc"));

        // —— Settings (Pulse + Windows appearance) ——
        Add("Dark mode", "Apps + system dark", "\uE708", "settings",
            QuickActionsService.SetDarkMode, "night", "dark theme");
        Add("Light mode", "Apps + system light", "\uE706", "settings",
            QuickActionsService.SetLightMode, "day", "light theme");
        Add("Personalization", "Themes and colors", "\uE790", "settings",
            () => AdminLauncher.Uri("ms-settings:personalization"));
        Add("Colors", "Accent color", "\uE790", "settings",
            () => AdminLauncher.Uri("ms-settings:colors"));
        Add("Overlay panel", "Size and opacity", "\uE713", "settings",
            PulseHost.ShowOverlayPanel, "widget", "opacity", "scale");
        Add("Confirm destructive actions", "Toggle safety prompts", "\uE72E", "settings",
            () =>
            {
                var s = SettingsService.Load();
                s.ConfirmDestructiveActions = !s.ConfirmDestructiveActions;
                SettingsService.Save(s);
            }, "safety");
        Add("Check for Pulse updates", "Portal update channel", "\uE895", "settings",
            () => UpdateService.CheckForUpdates(), "update");
        Add("MugoByte Account", "Sign in, license, devices", "\uE77B", "settings",
            () => PulseHost.ShowMain("account"), "account", "license", "sign in");
        Add("Sign out of MugoByte", "Clear secure session and activation", "\uE77B", "settings",
            () =>
            {
                _ = AppHost.Get<IActivationService>().SignOutAsync();
                new AccountGateWindow().ShowDialog();
            }, "logout", "sign out");
        Add("Open MugoByte Portal", "Account and billing", "\uE8A5", "settings",
            () => AccountBootstrap.OpenPortal("account"), "portal");
        Add("About Windows", "winver", "\uE946", "settings",
            () => AdminLauncher.Shell("winver"));
        Add("About Pulse", "Version and brand", "\uE946", "settings",
            () =>
            {
                var w = System.Windows.Application.Current.Windows.OfType<PulseMainWindow>().FirstOrDefault();
                new AboutWindow { Owner = w }.ShowDialog();
            }, "about");

        // —— Settings pages (search-heavy) ——
        foreach (var (title, uri, keys) in SettingsPages())
        {
            Add(title, uri, "\uE713", "settings",
                () => AdminLauncher.Uri(uri), keys);
        }

        return a;
    }

    private static IEnumerable<(string Title, string Uri, string[] Keys)> SettingsPages() =>
    [
        ("Display", "ms-settings:display", ["monitor", "resolution"]),
        ("Sound", "ms-settings:sound", ["audio", "volume"]),
        ("Notifications", "ms-settings:notifications", ["toast"]),
        ("Focus", "ms-settings:quiethours", ["dnd"]),
        ("Power & sleep", "ms-settings:powersleep", ["sleep"]),
        ("Storage", "ms-settings:storagesense", ["disk"]),
        ("Multitasking", "ms-settings:multitasking", ["snap"]),
        ("Clipboard", "ms-settings:clipboard", ["history"]),
        ("Date & time", "ms-settings:dateandtime", ["clock"]),
        ("Language", "ms-settings:regionlanguage", ["locale"]),
        ("Accessibility", "ms-settings:easeofaccess", ["a11y", "ease"]),
        ("Accounts", "ms-settings:yourinfo", ["profile"]),
        ("Windows Hello", "ms-settings:signinoptions", ["pin", "hello"]),
        ("For developers", "ms-settings:developers", ["dev mode"]),
        ("About PC", "ms-settings:about", ["system"]),
        ("Troubleshoot", "ms-settings:troubleshoot", ["fix"]),
        ("Activation", "ms-settings:activation", ["license"]),
        ("Find my device", "ms-settings:findmydevice", []),
        ("Game Mode", "ms-settings:gaming-gamemode", ["gaming"]),
        ("Xbox Game Bar", "ms-settings:gaming-gamebar", ["xbox"]),
    ];
}
