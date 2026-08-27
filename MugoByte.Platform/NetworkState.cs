using System.Net.NetworkInformation;

namespace MugoByte.Platform;

public static class NetworkState
{
    public static bool IsAvailable()
    {
        try { return NetworkInterface.GetIsNetworkAvailable(); }
        catch { return false; }
    }
}
