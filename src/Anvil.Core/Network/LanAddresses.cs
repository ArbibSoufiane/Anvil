using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Serilog;

namespace Anvil.Core.Network;

/// <summary>One network adapter as Windows reports it. Id is NetworkInterface.Id, e.g. "{4D36E972-...}".</summary>
public sealed record AdapterInfo(string Id, string Name, string Description, NetworkInterfaceType Type, bool Up, bool HasGateway,
    IReadOnlyList<IPAddress> IPv4);

/// <summary>A private IPv4 address of this PC on a real network, and the adapter that has it.</summary>
public sealed record LanAddress(string AdapterId, string AdapterName, IPAddress Address);

/// <summary>Finds the addresses other devices on the local network reach this PC at.</summary>
public static class LanAddresses
{
    private static readonly string[] VirtualMarks = ["Hyper-V", "vEthernet", "VMware", "VirtualBox", "WSL", "Loopback"];

    /// <summary>
    /// Private IPv4 addresses of connected adapters with a gateway, in adapter order. Hyper-V, WSL, VMware and VirtualBox
    /// adapters are skipped: no other device reaches this PC through them.
    /// </summary>
    public static IReadOnlyList<LanAddress> Pick(IEnumerable<AdapterInfo> adapters) =>
        adapters.Where(a => a.Up && a.HasGateway && !IsVirtual(a))
            .SelectMany(a => a.IPv4.Where(IsPrivate).Select(ip => new LanAddress(a.Id, a.Name, ip)))
            .ToList();

    public static IReadOnlyList<LanAddress> Find() => Pick(Adapters());

    /// <summary>10.0.0.0/8, 172.16.0.0/12 or 192.168.0.0/16.</summary>
    public static bool IsPrivate(IPAddress a)
    {
        if (a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    public static bool IsVirtual(AdapterInfo a) =>
        a.Type is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
        || VirtualMarks.Any(m => a.Name.Contains(m, StringComparison.OrdinalIgnoreCase) || a.Description.Contains(m, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<AdapterInfo> Adapters()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Select(n =>
            {
                var ip = n.GetIPProperties();
                return new AdapterInfo(n.Id, n.Name, n.Description, n.NetworkInterfaceType, n.OperationalStatus == OperationalStatus.Up,
                    ip.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any)),
                    ip.UnicastAddresses.Select(u => u.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList());
            }).ToList();
        }
        catch (NetworkInformationException e)
        {
            Log.Warning(e, "Network adapters could not be listed");
            return [];
        }
    }
}
