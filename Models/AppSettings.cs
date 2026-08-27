namespace CpuTempWidget.Models;

public sealed class AppSettings
{
    public double Left { get; set; } = 40;
    public double Top { get; set; } = 40;
    public bool IsLocked { get; set; }
    public bool StartWithWindows { get; set; } = true;
    public double FontSize { get; set; } = 13;
    public double Opacity { get; set; } = 0.96;
    public bool HasSeenWelcome { get; set; }
    public bool AutoHideSettings { get; set; } = true;
    public bool ConfirmDestructiveActions { get; set; } = true;

    /// <summary>When true, prefer mock portal clients (local validation).</summary>
    public bool UseMockAccount { get; set; }

    /// <summary>Optional future sync of theme/favorites to Portal (off by default).</summary>
    public bool SyncSettingsToPortal { get; set; }

    public bool HasCompletedAccountSetup { get; set; }

    /// <summary>Fade overlay contrast to stay readable on wallpaper / games. Cheap halo sample.</summary>
    public bool AdaptiveContrast { get; set; } = true;

    /// <summary>Strip (row), Quad (2×2 + center watts), Stack (vertical), Focus (large CPU/temp).</summary>
    public string OverlayLayout { get; set; } = "Strip";

    /// <summary>When false, overlay is hidden but Pulse stays alive via the tray icon.</summary>
    public bool WidgetVisible { get; set; } = true;

    /// <summary>Keep the overlay above other windows when shown.</summary>
    public bool AlwaysOnTop { get; set; } = true;

    /// <summary>
    /// One-time optional UAC for LHM CPU Package sensors (Ring0). Once true, Pulse will not
    /// prompt again — elevated <c>--lhm-sensor-probe</c> writes a shared cache the main process reads.
    /// </summary>
    public bool LhmElevationOffered { get; set; }

    /// <summary>One-time portal reconnect prompt when refresh token is missing.</summary>
    public bool PortalSignInPrompted { get; set; }
}
