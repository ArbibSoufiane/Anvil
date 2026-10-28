using Serilog;

namespace Anvil.Core.Elevation;

/// <summary>Reads the inbound Windows Firewall rules through HNetCfg.FwPolicy2, which needs no administrator rights.</summary>
public static class WindowsFirewallReader
{
    private const int Inbound = 1;
    private const int Allow = 1;

    /// <returns>The inbound rules, or none when Windows would not say.</returns>
    public static IReadOnlyList<ExistingFirewallRule> Read()
    {
        try
        {
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;
            var rules = new List<ExistingFirewallRule>();
            foreach (dynamic rule in policy.Rules)
            {
                if ((int)rule.Direction == Inbound) rules.Add(FromCom(rule));
            }

            return rules;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Warning(e, "Windows Firewall rules could not be read");
            return [];
        }
    }

    /// <summary>One HNetCfg.FWRule as a record.</summary>
    public static ExistingFirewallRule FromCom(dynamic rule) => new(
        (string?)rule.Name ?? "",
        (string?)rule.ApplicationName,
        (int)rule.Protocol,
        (string?)rule.LocalPorts,
        (int)rule.Profiles,
        (string?)rule.RemoteAddresses,
        (int)rule.Action == Allow,
        (bool)rule.Enabled);
}
