using Anvil.Core.Preflight;

namespace Anvil.Core.Network;

/// <summary>What keeps other devices from reaching this PC while something is open to the network.</summary>
public static class NetworkFindings
{
    public const string FirewallId = "firewall-missing";
    public const string PublicId = "network-public";
    public const string DnsBindPrefix = "dns-bind-";

    /// <param name="anyOpen">htdocs or an enabled site is open to the network; with nothing open there is nothing to report.</param>
    /// <param name="publicAddresses">This PC's LAN addresses on networks Windows treats as Public.</param>
    public static IReadOnlyList<PreflightFinding> For(bool anyOpen, bool firewallReady, IReadOnlyList<LanAddress> publicAddresses,
        IReadOnlyList<DnsBindFailure> bindFailures)
    {
        if (!anyOpen) return [];
        var findings = new List<PreflightFinding>();
        if (!firewallReady)
            findings.Add(new PreflightFinding(FirewallId, Severity.Warning,
                "Other devices cannot reach this PC yet: Windows Firewall does not let them in.",
                "Open the firewall for Anvil (Windows asks for permission)."));
        if (publicAddresses.Count > 0)
            findings.Add(new PreflightFinding(PublicId, Severity.Warning,
                $"Windows treats this network as Public, so other devices are blocked ({string.Join(", ", publicAddresses.Select(a => a.AdapterName).Distinct())}).",
                "Switch the network to Private in Windows' network settings."));
        foreach (var failure in bindFailures)
            findings.Add(new PreflightFinding(DnsBindPrefix + failure.Address, Severity.Warning,
                $"Network names are off on {failure.Address}: port 53 is in use there.", $"Sites still open at http://{failure.Address}/<folder>."));
        return findings;
    }
}
