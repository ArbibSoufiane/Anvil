using System.Net;

namespace Anvil.Core.Certificates;

/// <summary>Host name matching for certificates: a wildcard stands for exactly one leftmost label, IP addresses match only exactly.</summary>
public static class NameMatch
{
    public static bool Covers(IReadOnlyList<string> certNames, string host) => certNames.Any(name => Matches(name, host));

    private static bool Matches(string certName, string host)
    {
        if (string.Equals(certName, host, StringComparison.OrdinalIgnoreCase)) return true;
        if (!certName.StartsWith("*.", StringComparison.Ordinal) || IPAddress.TryParse(host, out _)) return false;
        var dot = host.IndexOf('.');
        return dot > 0 && string.Equals(host[dot..], certName[1..], StringComparison.OrdinalIgnoreCase);
    }
}
