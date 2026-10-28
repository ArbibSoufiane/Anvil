using System.Net;
using System.Net.NetworkInformation;
using Anvil.Core.Network;

namespace Anvil.Core.Tests.Network;

public sealed class LanAddressesTests
{
    private static AdapterInfo A(string name, string desc, string ip, bool gateway, bool up = true, NetworkInterfaceType type = NetworkInterfaceType.Ethernet) =>
        new($"{{{name}}}", name, desc, type, up, gateway, [IPAddress.Parse(ip)]);

    // The owner's PC: one real Ethernet card among Hyper-V, WSL and VMware adapters.
    [Fact]
    public void Picks_private_addresses_on_real_adapters_with_a_gateway()
    {
        var picked = LanAddresses.Pick(
        [
            A("Ethernet", "Intel(R) Ethernet Connection", "192.168.100.254", gateway: true),
            A("vEthernet (WSL (Hyper-V firewall))", "Hyper-V Virtual Ethernet Adapter", "172.18.0.1", gateway: false),
            A("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter #2", "172.21.128.1", gateway: true),
            A("VMware Network Adapter VMnet8", "VMware Virtual Ethernet Adapter for VMnet8", "192.168.108.1", gateway: true),
            A("Wi-Fi", "Wireless", "169.254.1.74", gateway: false, type: NetworkInterfaceType.Wireless80211),
            A("Ethernet 3", "USB LAN", "10.0.0.7", gateway: true, up: false),
            A("Ethernet 4", "Public card", "8.8.4.4", gateway: true),
        ]);

        Assert.Equal(["192.168.100.254"], picked.Select(p => p.Address.ToString()));
        Assert.Equal(new LanAddress("{Ethernet}", "Ethernet", IPAddress.Parse("192.168.100.254")), picked[0]);
    }

    [Fact]
    public void Wired_and_wireless_cards_both_count_and_keep_adapter_order() =>
        Assert.Equal(["10.1.2.3", "172.16.0.9"], LanAddresses.Pick(
        [
            A("Ethernet", "Realtek PCIe GbE", "10.1.2.3", gateway: true),
            A("Wi-Fi", "Intel(R) Wi-Fi 6E AX211", "172.16.0.9", gateway: true, type: NetworkInterfaceType.Wireless80211),
            A("VirtualBox Host-Only Network", "VirtualBox Host-Only Ethernet Adapter", "192.168.56.1", gateway: true),
            A("Loopback Pseudo-Interface 1", "Software Loopback Interface 1", "127.0.0.1", gateway: false, type: NetworkInterfaceType.Loopback),
        ]).Select(p => p.Address.ToString()));

    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("172.15.0.1", false)]
    [InlineData("192.168.1.5", true)]
    [InlineData("192.169.1.5", false)]
    [InlineData("169.254.1.74", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("fd00::1", false)]
    public void Private_means_the_three_ipv4_ranges(string ip, bool expected) =>
        Assert.Equal(expected, LanAddresses.IsPrivate(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("vEthernet (Default Switch)", "x", NetworkInterfaceType.Ethernet, true)]
    [InlineData("x", "VMware Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet, true)]
    [InlineData("x", "VirtualBox Host-Only Ethernet Adapter", NetworkInterfaceType.Ethernet, true)]
    [InlineData("WSL", "x", NetworkInterfaceType.Ethernet, true)]
    [InlineData("x", "hyper-v virtual ethernet adapter", NetworkInterfaceType.Ethernet, true)]
    [InlineData("x", "x", NetworkInterfaceType.Tunnel, true)]
    [InlineData("x", "x", NetworkInterfaceType.Loopback, true)]
    [InlineData("Ethernet", "Intel(R) Ethernet Connection", NetworkInterfaceType.Ethernet, false)]
    public void Virtual_adapters_are_recognised(string name, string desc, NetworkInterfaceType type, bool expected) =>
        Assert.Equal(expected, LanAddresses.IsVirtual(new AdapterInfo("{x}", name, desc, type, true, true, [])));
}
