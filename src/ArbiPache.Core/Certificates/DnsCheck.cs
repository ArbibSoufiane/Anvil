using System.Net;
using System.Net.Sockets;

namespace ArbiPache.Core.Certificates;

/// <summary>Where a domain points today, so the person can see it reaches this server before Let's Encrypt is asked.</summary>
public static class DnsCheck
{
    /// <summary>The addresses <paramref name="name"/> resolves to, IPv4 first; empty when it does not resolve.</summary>
    public static async Task<IReadOnlyList<string>> ResolveAsync(string name, CancellationToken ct)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(name, ct);
            return addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).Select(a => a.ToString()).Distinct().ToList();
        }
        catch (SocketException)
        {
            return [];
        }
    }
}
