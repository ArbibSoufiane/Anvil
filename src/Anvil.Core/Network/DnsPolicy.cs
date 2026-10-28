namespace Anvil.Core.Network;

/// <summary>
/// What the name service answers: this PC's address for every name under the network domain, claimed by a site or not
/// (Apache's catch-all answers the rest), an empty answer for other record types, and a refusal for any other domain.
/// </summary>
public static class DnsPolicy
{
    public const uint Ttl = 60;

    public static (DnsReplyCode Code, bool WithAddress) Decide(DnsQuestion q, string networkDomain)
    {
        var ours = q.Class == DnsMessage.ClassIn
                   && (q.Name == networkDomain || q.Name.EndsWith("." + networkDomain, StringComparison.Ordinal));
        if (!ours) return (DnsReplyCode.Refused, false);
        return (DnsReplyCode.NoError, q.Type == DnsMessage.TypeA);
    }
}
