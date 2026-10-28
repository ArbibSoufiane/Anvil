using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Anvil.App.Services;

public static class LanAddress
{
    /// <summary>This PC's private IPv4 address on a connected network (one with a gateway first), or null.</summary>
    public static IPAddress? Find()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Select(n => n.GetIPProperties())
                .OrderByDescending(p => p.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any)))
                .SelectMany(p => p.UnicastAddresses)
                .Select(u => u.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a));
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private static bool IsPrivate(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }
}
