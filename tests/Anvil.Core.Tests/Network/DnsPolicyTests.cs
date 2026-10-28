using Anvil.Core.Network;

namespace Anvil.Core.Tests.Network;

public sealed class DnsPolicyTests
{
    [Theory]
    [InlineData("shop.arbib.pc", DnsMessage.TypeA, DnsMessage.ClassIn, DnsReplyCode.NoError, true)]
    [InlineData("arbib.pc", DnsMessage.TypeA, DnsMessage.ClassIn, DnsReplyCode.NoError, true)]
    [InlineData("never.added.arbib.pc", DnsMessage.TypeA, DnsMessage.ClassIn, DnsReplyCode.NoError, true)]
    [InlineData("shop.arbib.pc", DnsMessage.TypeAaaa, DnsMessage.ClassIn, DnsReplyCode.NoError, false)]
    [InlineData("shop.arbib.pc", (ushort)15, DnsMessage.ClassIn, DnsReplyCode.NoError, false)]
    [InlineData("notarbib.pc", DnsMessage.TypeA, DnsMessage.ClassIn, DnsReplyCode.Refused, false)]
    [InlineData("pc", DnsMessage.TypeA, DnsMessage.ClassIn, DnsReplyCode.Refused, false)]
    [InlineData("example.com", DnsMessage.TypeA, DnsMessage.ClassIn, DnsReplyCode.Refused, false)]
    [InlineData("shop.arbib.pc", DnsMessage.TypeA, (ushort)3, DnsReplyCode.Refused, false)]
    public void Decides(string name, ushort type, ushort cls, DnsReplyCode code, bool withAddress) =>
        Assert.Equal((code, withAddress), DnsPolicy.Decide(new DnsQuestion(1, true, name, type, cls, Array.Empty<byte>()), "arbib.pc"));
}
