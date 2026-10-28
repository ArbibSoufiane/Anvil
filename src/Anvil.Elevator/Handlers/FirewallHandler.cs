using Anvil.Core.Elevation;
using Anvil.Core.Paths;

namespace Anvil.Elevator.Handlers;

/// <summary>
/// firewall-set: removes every rule for Anvil's programs, its own and those Windows made from its "Allow access?" prompt,
/// then adds the planned ones, through HNetCfg.FwPolicy2.
/// </summary>
internal static class FirewallHandler
{
    private const int Inbound = 1;
    private const int Allow = 1;

    public static ElevatedResult Run(FirewallSetRequest request, AppPaths paths)
    {
        var planned = FirewallRulePlanner.Plan(request, paths);
        dynamic policy = Create("HNetCfg.FwPolicy2");
        dynamic rules = policy.Rules;

        var stale = new List<dynamic>();
        foreach (dynamic rule in rules)
        {
            if (FirewallRulePlanner.IsOurs(WindowsFirewallReader.FromCom(rule), paths)) stale.Add(rule);
        }

        // Rules.Remove takes a name, and another program's rule may share it ("Apache HTTP Server"): each stale rule gets a
        // name of its own first, so only that rule goes.
        foreach (var rule in stale)
        {
            var unique = FirewallRulePlanner.RemovingName();
            rule.Name = unique;
            rules.Remove(unique);
        }

        foreach (var plan in planned)
        {
            dynamic rule = Create("HNetCfg.FWRule");
            rule.Name = plan.Name;
            rule.Description = plan.Name == FirewallRulePlanner.NameServiceRule
                ? "Added by Anvil for its name service."
                : "Added by Anvil for its web server.";
            rule.Grouping = "Anvil";
            rule.ApplicationName = plan.Program;
            rule.Protocol = (int)plan.Protocol;
            rule.LocalPorts = plan.LocalPorts;
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
