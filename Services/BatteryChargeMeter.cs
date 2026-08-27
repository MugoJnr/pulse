using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;

namespace CpuTempWidget.Services;

/// <summary>
/// Live charger power in watts from the Windows battery class driver (IOCTL),
/// with ACPI BatteryStatus WMI as fallback. Firmware reports milliwatts.
/// </summary>
public static class BatteryChargeMeter
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const uint IoctlBatteryQueryTag = 0x00294040;
    private const uint IoctlBatteryQueryStatus = 0x0029404C;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;
    private const int BatteryUnknownRate = unchecked((int)0x80000000);

    private static readonly Guid BatteryInterface =
        new(0x72631e54, 0x78A4, 0x11d0, 0xbc, 0xf7, 0x00, 0xaa, 0x00, 0xb7, 0xb3, 0x2a);

    public static double? ReadChargeWatts(bool chargerConnected)
    {
        var sample = TryReadIoctl() ?? TryReadWmi();
        if (sample is null)
            return chargerConnected ? 0 : null;

        var watts = sample.Value;
        if (watts is < -250 or > 250)
            return chargerConnected ? 0 : null;

        // Positive = charging the pack. Zero on AC = hold / conservation / full.
        if (watts <= 0)
            return chargerConnected ? 0 : null;

        return Math.Round(watts, 1);
    }

    private static double? TryReadIoctl()
    {
        var info = IntPtr.Zero;
        try
        {
            var guid = BatteryInterface;
            info = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
            if (info == IntPtr.Zero || info == new IntPtr(-1))
                return null;

            double? best = null;
            for (uint i = 0; i < 8; i++)
            {
                var iface = new SpDeviceInterfaceData { CbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(info, IntPtr.Zero, ref guid, i, ref iface))
                    break;

                var watts = ReadInterfaceWatts(info, ref iface);
                if (watts is > 0)
                    best = best is null ? watts : best + watts;
            }

            return best;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (info != IntPtr.Zero && info != new IntPtr(-1))
                SetupDiDestroyDeviceInfoList(info);
        }
    }

    private static double? ReadInterfaceWatts(IntPtr info, ref SpDeviceInterfaceData iface)
    {
        SetupDiGetDeviceInterfaceDetail(info, ref iface, IntPtr.Zero, 0, out var required, IntPtr.Zero);
        if (required < 8)
            return null;

        var detail = Marshal.AllocHGlobal((int)required);
        var handle = IntPtr.Zero;
        try
        {
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(info, ref iface, detail, required, out _, IntPtr.Zero))
                return null;

            var path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
            if (string.IsNullOrWhiteSpace(path))
                return null;

            handle = CreateFile(path, GenericRead, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                return null;

            uint tag = 0;
            uint returned = 0;
            if (!DeviceIoControlTag(handle, IoctlBatteryQueryTag, IntPtr.Zero, 0,
                    ref tag, sizeof(uint), ref returned, IntPtr.Zero) || tag == 0)
                return null;

            var wait = new BatteryWaitStatus
            {
                BatteryTag = tag,
                Timeout = 0,
                HighCapacity = uint.MaxValue
            };
            var status = new BatteryStatus();
            returned = 0;
            if (!DeviceIoControlStatus(handle, IoctlBatteryQueryStatus, ref wait,
                    (uint)Marshal.SizeOf<BatteryWaitStatus>(), ref status,
                    (uint)Marshal.SizeOf<BatteryStatus>(), ref returned, IntPtr.Zero))
                return null;

            if (status.Rate == BatteryUnknownRate)
                return null;

            // milliwatts; 0 is valid (plugged in, not filling the pack)
            return status.Rate / 1000.0;
        }
        finally
        {
            if (handle != IntPtr.Zero && handle != new IntPtr(-1))
                CloseHandle(handle);
            Marshal.FreeHGlobal(detail);
        }
    }

    private static double? TryReadWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI",
                "SELECT ChargeRate, DischargeRate, Charging, PowerOnline FROM BatteryStatus");
            using var results = searcher.Get();
            double? charge = null;
            foreach (ManagementObject obj in results)
            {
                using (obj)
                {
                    long mw = 0;
                    try { mw = Convert.ToInt64(obj["ChargeRate"] ?? 0, CultureInfo.InvariantCulture); }
                    catch { }
                    if (mw > 0)
                        charge = charge is null ? mw / 1000.0 : charge + mw / 1000.0;
                    else if (charge is null)
                        charge = 0;
                }
            }

            return charge;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
        uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize,
        out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    private static extern bool DeviceIoControlTag(
        IntPtr hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        ref uint lpOutBuffer, uint nOutBufferSize,
        ref uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    private static extern bool DeviceIoControlStatus(
        IntPtr hDevice, uint dwIoControlCode,
        ref BatteryWaitStatus lpInBuffer, uint nInBufferSize,
        ref BatteryStatus lpOutBuffer, uint nOutBufferSize,
        ref uint lpBytesReturned, IntPtr lpOverlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryWaitStatus
    {
        public uint BatteryTag;
        public uint Timeout;
        public uint PowerState;
        public uint LowCapacity;
        public uint HighCapacity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryStatus
    {
        public uint PowerState;
        public uint Capacity;
        public uint Voltage;
        public int Rate;
    }
}
