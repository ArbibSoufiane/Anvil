using System.Net;
using Anvil.Core.Network;

namespace Anvil.Core.Tests.Network;

public sealed class DnsMessageTests
{
    [Fact]
    public void Parses_a_plain_query()
    {
        var q = DnsMessage.TryParse(DnsPackets.Query(0x1234, "shop.arbib.pc", DnsMessage.TypeA))!;

        Assert.Equal((ushort)0x1234, q.Id);
        Assert.Equal("shop.arbib.pc", q.Name);
        Assert.Equal(DnsMessage.TypeA, q.Type);
        Assert.Equal(DnsMessage.ClassIn, q.Class);
        Assert.True(q.RecursionDesired);
        Assert.Equal(15 + 4, q.RawQuestion.Length);
    }

    [Fact]
    public void Reply_with_address_has_one_authoritative_answer()
    {
        var q = DnsMessage.TryParse(DnsPackets.Query(7, "shop.arbib.pc", DnsMessage.TypeA))!;

        var r = DnsMessage.Reply(q, DnsReplyCode.NoError, IPAddress.Parse("192.168.100.254"), 60);

        Assert.Equal(new byte[] { 0x00, 0x07, 0x85, 0x00, 0, 1, 0, 1, 0, 0, 0, 0 }, r[..12]); // QR AA RD, NOERROR, QD=1 AN=1
        Assert.Equal(new byte[] { 0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 192, 168, 100, 254 }, r[^16..]);
        Assert.Equal(12 + q.RawQuestion.Length + 16, r.Length);
    }

    [Fact]
    public void Reply_keeps_recursion_desired_as_asked()
    {
        var q = DnsMessage.TryParse(DnsPackets.Query(7, "shop.arbib.pc", DnsMessage.TypeA, recursionDesired: false))!;

        Assert.False(q.RecursionDesired);
        Assert.Equal(0x84, DnsMessage.Reply(q, DnsReplyCode.NoError, IPAddress.Loopback, 60)[2]);
    }

    [Fact]
    public void Refused_reply_has_no_answer()
    {
        var r = DnsMessage.Reply(DnsMessage.TryParse(DnsPackets.Query(7, "example.com", DnsMessage.TypeA))!, DnsReplyCode.Refused, null, 60);

        Assert.Equal(0x05, r[3] & 0x0F);
        Assert.Equal(0, r[7]);
    }

    [Fact]
    public void No_address_reply_for_aaaa_is_an_empty_answer()
    {
        var q = DnsMessage.TryParse(DnsPackets.Query(7, "shop.arbib.pc", DnsMessage.TypeAaaa))!;

        var r = DnsMessage.Reply(q, DnsReplyCode.NoError, null, 60);

        Assert.Equal(0, r[3] & 0x0F);
        Assert.Equal(0, r[7]);
        Assert.Equal(12 + q.RawQuestion.Length, r.Length);
    }

    [Fact]
    public void Mixed_case_query_is_answered_and_echoed_exactly()
    {
        var packet = DnsPackets.Query(9, "ShOp.ArBiB.pC", DnsMessage.TypeA);
        var q = DnsMessage.TryParse(packet)!;

        Assert.Equal("shop.arbib.pc", q.Name);
        var r = DnsMessage.Reply(q, DnsReplyCode.NoError, IPAddress.Loopback, 60);
        Assert.Equal(packet.AsSpan(12, q.RawQuestion.Length).ToArray(), r.AsSpan(12, q.RawQuestion.Length).ToArray());
    }

    [Fact]
    public void Query_with_edns_record_is_answered()
    {
        var q = DnsMessage.TryParse(DnsPackets.Query(3, "shop.arbib.pc", DnsMessage.TypeA, edns: true))!;

        var r = DnsMessage.Reply(q, DnsReplyCode.NoError, IPAddress.Loopback, 60);

        Assert.Equal(0, r[11]); // ARCOUNT 0
        Assert.Equal(new byte[] { 127, 0, 0, 1 }, r[^4..]);
    }

    public static TheoryData<string, byte[]> Malformed()
    {
        var plain = DnsPackets.Query(1, "shop.arbib.pc", DnsMessage.TypeA);
        byte[] pointer = [0, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 0x0C, 0, 1, 0, 1];
        return new TheoryData<string, byte[]>
        {
            { "empty", [] },
            { "11 bytes", plain[..11] },
            { "a response", DnsPackets.With(plain, 2, 0x81) },
            { "opcode 2", DnsPackets.With(plain, 2, (2 << 3) | 0x01) },
            { "no question", DnsPackets.With(plain, 5, 0) },
            { "two questions", DnsPackets.With(plain, 5, 2) },
            { "a 64-byte label", DnsPackets.Query(1, new string('a', 64) + ".pc", DnsMessage.TypeA) },
            { "a compression pointer", pointer },
            { "a 257-byte name", DnsPackets.Query(1, string.Join(".", Enumerable.Repeat(new string('a', 63), 4)), DnsMessage.TypeA) },
            { "a cut question", plain[..^2] },
            { "a name without its end", plain[..17] },
        };
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Malformed_packets_are_ignored(string what, byte[] packet) => Assert.True(DnsMessage.TryParse(packet) is null, what);
}
