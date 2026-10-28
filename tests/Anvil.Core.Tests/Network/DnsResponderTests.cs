using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Anvil.Core.Network;

namespace Anvil.Core.Tests.Network;

// Real sockets on loopback, on a port free on every address when the test starts.
public sealed class DnsResponderTests
{
    private static readonly IPAddress Second = IPAddress.Parse("127.0.0.2");
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly int _port = FreePort();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int FreePort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Any, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private async Task<byte[]> AskAsync(IPAddress server, string name, ushort type = DnsMessage.TypeA)
    {
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await client.SendAsync(DnsPackets.Query(0x4242, name, type), new IPEndPoint(server, _port), Ct);
        return (await client.ReceiveAsync(Ct).AsTask().WaitAsync(Wait, Ct)).Buffer;
    }

    private async Task SendRawAsync(IPAddress server, byte[] packet)
    {
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await client.SendAsync(packet, new IPEndPoint(server, _port), Ct);
    }

    [Fact]
    public async Task Answers_with_the_address_it_listens_on()
    {
        await using var dns = new DnsResponder(_port);

        Assert.Empty(await dns.ApplyAsync("arbib.pc", [IPAddress.Loopback], Ct));

        var reply = await AskAsync(IPAddress.Loopback, "shop.arbib.pc");
        Assert.Equal(new byte[] { 0x42, 0x42 }, reply[..2]);
        Assert.Equal(new byte[] { 127, 0, 0, 1 }, reply[^4..]);
    }

    [Fact]
    public async Task Refuses_names_outside_the_domain()
    {
        await using var dns = new DnsResponder(_port);
        await dns.ApplyAsync("arbib.pc", [IPAddress.Loopback], Ct);

        Assert.Equal(5, (await AskAsync(IPAddress.Loopback, "example.com"))[3] & 0x0F);
    }

    [Fact]
    public async Task A_new_domain_is_answered_without_a_rebind()
    {
        await using var dns = new DnsResponder(_port);
        await dns.ApplyAsync("arbib.pc", [IPAddress.Loopback], Ct);

        await dns.ApplyAsync("team.lan", [IPAddress.Loopback], Ct);

        Assert.Equal(1, (await AskAsync(IPAddress.Loopback, "shop.team.lan"))[7]);
        Assert.Equal(5, (await AskAsync(IPAddress.Loopback, "shop.arbib.pc"))[3] & 0x0F);
    }

    [Fact]
    public void Accepts_only_private_askers()
    {
        Assert.True(DnsResponder.Accepts(IPAddress.Parse("192.168.1.20")));
        Assert.True(DnsResponder.Accepts(IPAddress.Parse("10.4.0.9")));
        Assert.True(DnsResponder.Accepts(IPAddress.Loopback));
        Assert.True(DnsResponder.Accepts(Second));
        Assert.False(DnsResponder.Accepts(IPAddress.Parse("8.8.8.8")));
        Assert.False(DnsResponder.Accepts(IPAddress.Parse("169.254.1.74")));
    }

    [Fact]
    public async Task Malformed_packet_does_not_stop_it()
    {
        await using var dns = new DnsResponder(_port);
        await dns.ApplyAsync("arbib.pc", [IPAddress.Loopback], Ct);

        await SendRawAsync(IPAddress.Loopback, [1, 2, 3]);

        Assert.Equal(1, (await AskAsync(IPAddress.Loopback, "shop.arbib.pc"))[7]);
    }

    [Fact]
    public async Task Datagrams_too_big_for_a_query_are_dropped_without_a_pause()
    {
        await using var dns = new DnsResponder(_port);
        await dns.ApplyAsync("arbib.pc", [IPAddress.Loopback], Ct);
        for (var i = 0; i < 10; i++) await SendRawAsync(IPAddress.Loopback, new byte[4500]);

        var watch = Stopwatch.StartNew();
        Assert.Equal(1, (await AskAsync(IPAddress.Loopback, "shop.arbib.pc"))[7]);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(500), $"answered after {watch.Elapsed.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public async Task Client_that_vanished_does_not_stop_it()
    {
        await using var dns = new DnsResponder(_port);
        await dns.ApplyAsync("arbib.pc", [IPAddress.Loopback], Ct);
        using (var gone = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            await gone.SendAsync(DnsPackets.Query(1, "shop.arbib.pc", DnsMessage.TypeA), new IPEndPoint(IPAddress.Loopback, _port), Ct);

        await Task.Delay(200, Ct); // the reply hits a closed port; Windows reports it back to the listener

        Assert.Equal(1, (await AskAsync(IPAddress.Loopback, "shop.arbib.pc"))[7]);
    }

    [Fact]
    public async Task Busy_address_is_reported_and_others_answer()
    {
        using var squatter = new UdpClient { ExclusiveAddressUse = true };
        squatter.Client.Bind(new IPEndPoint(IPAddress.Loopback, _port));
        await using var dns = new DnsResponder(_port);

        var failures = await dns.ApplyAsync("arbib.pc", [IPAddress.Loopback, Second], Ct);

        var failure = Assert.Single(failures);
        Assert.Equal(IPAddress.Loopback, failure.Address);
        Assert.False(string.IsNullOrWhiteSpace(failure.Reason));
        Assert.Equal([Second], dns.Listening);
        Assert.Equal(new byte[] { 127, 0, 0, 2 }, (await AskAsync(Second, "shop.arbib.pc"))[^4..]);
    }

    [Fact]
    public async Task Moves_to_new_addresses_and_stops_without_a_domain()
    {
        await using var dns = new DnsResponder(_port);
        await dns.ApplyAsync("arbib.pc", [IPAddress.Loopback], Ct);

        await dns.ApplyAsync("arbib.pc", [Second], Ct);
        Assert.Equal([Second], dns.Listening);
        using (new UdpClient(new IPEndPoint(IPAddress.Loopback, _port))) { } // 127.0.0.1 is free again

        await dns.ApplyAsync(null, [Second], Ct);
        Assert.Empty(dns.Listening);
        using var rebind = new UdpClient(new IPEndPoint(Second, _port)); // the port is free again
    }
}
