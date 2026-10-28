using System.Net;
using Anvil.Core.Network;
using Anvil.Core.Preflight;

namespace Anvil.Core.Tests.Network;

public sealed class NetworkFindingsTests
{
    private static LanAddress Lan(string adapter, string address = "192.168.100.254") =>
        new("{" + adapter.ToUpperInvariant() + "}", adapter, IPAddress.Parse(address));

    [Fact]
    public void Nothing_open_nothing_to_say() =>
        Assert.Empty(NetworkFindings.For(anyOpen: false, firewallReady: false, [Lan("Ethernet")], []));

    [Fact]
    public void Missing_rules_and_public_network_are_reported()
    {
        var f = NetworkFindings.For(true, false, [Lan("Ethernet")], []);

        Assert.Equal(new PreflightFinding("firewall-missing", Severity.Warning,
            "Other devices cannot reach this PC yet: Windows Firewall does not let them in.",
            "Open the firewall for Anvil (Windows asks for permission)."), f[0]);
        Assert.Equal(new PreflightFinding("network-public", Severity.Warning,
            "Windows treats this network as Public, so other devices are blocked (Ethernet).",
            "Switch the network to Private in Windows' network settings."), f[1]);
    }

    [Fact]
    public void Busy_port_53_is_reported_per_address() =>
        Assert.Equal(new PreflightFinding("dns-bind-192.168.100.254", Severity.Warning,
                "Network names are off on 192.168.100.254: port 53 is in use there.", "Sites still open at http://192.168.100.254/<folder>."),
            Assert.Single(NetworkFindings.For(true, true, [], [new DnsBindFailure(IPAddress.Parse("192.168.100.254"), "in use")])));

    [Fact]
    public void Each_public_adapter_is_named_once() =>
        Assert.Equal("Windows treats this network as Public, so other devices are blocked (Ethernet, Wi-Fi).",
            Assert.Single(NetworkFindings.For(true, true, [Lan("Ethernet"), Lan("Ethernet", "192.168.100.9"), Lan("Wi-Fi", "10.0.0.4")], [])).Message);
}
