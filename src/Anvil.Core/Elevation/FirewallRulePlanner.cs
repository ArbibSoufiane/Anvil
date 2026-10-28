namespace Anvil.Core.Elevation;

/// <summary>An inbound TCP allow rule. Profiles are NET_FW_PROFILE2 flags.</summary>
public sealed record FirewallRule(string Name, int Port, int Profiles, string RemoteAddresses);

/// <summary>The Windows Firewall rules for the web ports: local network (Private and Domain profiles, local subnet) or the internet.</summary>
public static class FirewallRulePlanner
{
    public const string NamePrefix = "Anvil HTTP ";

    private const int Domain = 1;
    private const int Private = 2;
    private const int AllProfiles = 0x7FFFFFFF;

    public static IReadOnlyList<FirewallRule> Plan(FirewallSetRequest r)
    {
        foreach (var port in r.Ports)
        {
            if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(r), port, "Ports are 1 to 65535.");
        }

        var (profiles, remote) = r.Scope == FirewallScope.Internet ? (AllProfiles, "*") : (Domain | Private, "LocalSubnet");
        return r.Ports.Distinct().Select(port => new FirewallRule(NamePrefix + port, port, profiles, remote)).ToList();
    }
}
