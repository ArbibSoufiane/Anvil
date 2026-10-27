using ArbiPache.Core.Elevation;

namespace ArbiPache.Elevator.Handlers;

/// <summary>firewall-set: removes every "ArbiPache HTTP *" rule, then adds the planned ones, through HNetCfg.FwPolicy2.</summary>
internal static class FirewallHandler
{
    private const int Tcp = 6;
    private const int Inbound = 1;
    private const int Allow = 1;

    public static ElevatedResult Run(FirewallSetRequest request)
    {
        var planned = FirewallRulePlanner.Plan(request);
        dynamic policy = Create("HNetCfg.FwPolicy2");
        dynamic rules = policy.Rules;

        // Removing by prefix also drops rules for ports ArbiPache no longer uses.
        var stale = new List<string>();
        foreach (dynamic rule in rules)
        {
            string name = rule.Name;
            if (name.StartsWith(FirewallRulePlanner.NamePrefix, StringComparison.Ordinal)) stale.Add(name);
        }

        foreach (var name in stale.Distinct()) rules.Remove(name);

        foreach (var plan in planned)
        {
            dynamic rule = Create("HNetCfg.FWRule");
            rule.Name = plan.Name;
            rule.Description = "Added by ArbiPache for its web server.";
            rule.Grouping = "ArbiPache";
            rule.Protocol = Tcp;
            rule.LocalPorts = plan.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            rule.Direction = Inbound;
            rule.Action = Allow;
            rule.Profiles = plan.Profiles;
            rule.RemoteAddresses = plan.RemoteAddresses;
            rule.Enabled = true;
            rules.Add(rule);
        }

        return new ElevatedResult(true, null);
    }

    private static object Create(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId, throwOnError: true)!)!;
}
