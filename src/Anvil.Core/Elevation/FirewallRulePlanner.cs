using Anvil.Core.Paths;
using Anvil.Core.Settings;
using Anvil.Core.Sites;

namespace Anvil.Core.Elevation;

/// <summary>IP protocol numbers, as Windows Firewall takes them.</summary>
public enum FirewallProtocol { Tcp = 6, Udp = 17 }

/// <summary>An inbound allow rule for one program. Profiles are NET_FW_PROFILE2 flags.</summary>
public sealed record FirewallRule(string Name, string Program, FirewallProtocol Protocol, string LocalPorts, int Profiles, string RemoteAddresses);

/// <summary>An inbound rule as Windows Firewall has it. Program is null for a rule that names no program.</summary>
public sealed record ExistingFirewallRule(string Name, string? Program, int Protocol, string? LocalPorts, int Profiles, string? RemoteAddresses,
    bool Allow, bool Enabled);

/// <summary>
/// Anvil's Windows Firewall rules: one per program and port, on the Private and Domain profiles and the local subnet only,
/// so nothing is reachable from a network Windows calls Public, nor from the internet.
/// </summary>
public static class FirewallRulePlanner
{
    public const string NamePrefix = "Anvil ";
    public const string WebServerRule = "Anvil web server";
    public const string NameServiceRule = "Anvil name service";
    public const int PrivateAndDomain = 3;

    private const int Public = 4;
    private const string LocalSubnet = "LocalSubnet";

    public static string HttpdPath(AppPaths paths, string apacheVersion) => Path.Combine(paths.ApacheRuntime(apacheVersion), "bin", "httpd.exe");

    public static string AgentPath(AppPaths paths) => Path.Combine(paths.Root, "Anvil.Agent.exe");

    public static IReadOnlyList<FirewallRule> Plan(FirewallSetRequest r, AppPaths paths)
    {
        foreach (var port in r.Ports)
        {
            if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(r), port, "Ports are 1 to 65535.");
        }

        var rules = new List<FirewallRule>();
        if (r.ApacheVersion is { } version && r.Ports.Count > 0)
            rules.Add(new FirewallRule(WebServerRule, HttpdPath(paths, version), FirewallProtocol.Tcp,
                string.Join(",", r.Ports.Distinct()), PrivateAndDomain, LocalSubnet));
        if (r.NameService)
            rules.Add(new FirewallRule(NameServiceRule, AgentPath(paths), FirewallProtocol.Udp, "53", PrivateAndDomain, LocalSubnet));
        return rules;
    }

    /// <summary>What must be open while htdocs or an enabled domain site is on the network; null when nothing is.</summary>
    public static FirewallSetRequest? Needed(AppSettings s, IReadOnlyList<SiteDefinition> sites)
    {
        var open = s.Network.HtdocsOnNetwork || sites.Any(x => x.Enabled && x.Kind == SiteKind.Domain && x.Access == AccessLevel.Network);
        return open ? new FirewallSetRequest(s.ActiveApache, [s.HttpPort, s.HttpsPort], NameService: s.NetworkDomain is not null) : null;
    }

    /// <summary>
    /// A rule Anvil may replace: one it named, or one for its own programs, such as the rule Windows makes when someone
    /// answers its "Allow access?" prompt for an Apache under runtime\apache.
    /// </summary>
    public static bool IsOurs(ExistingFirewallRule rule, AppPaths paths)
    {
        if (rule.Name.StartsWith(NamePrefix, StringComparison.OrdinalIgnoreCase)) return true;
        if (rule.Program is not { } program || !Path.IsPathFullyQualified(program)) return false;
        var full = Path.GetFullPath(program);
        if (string.Equals(full, Path.GetFullPath(AgentPath(paths)), StringComparison.OrdinalIgnoreCase)) return true;
        var apache = Path.GetFullPath(Path.Combine(paths.Runtime, "apache")) + Path.DirectorySeparatorChar;
        return full.StartsWith(apache, StringComparison.OrdinalIgnoreCase)
               && string.Equals(Path.GetFileName(full), "httpd.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every planned rule is there and on, and no other rule for Anvil's programs blocks them or opens them on Public
    /// networks: a block rule beats every allow rule.
    /// </summary>
    public static bool Satisfied(IReadOnlyList<FirewallRule> planned, IReadOnlyList<ExistingFirewallRule> existing, AppPaths paths)
    {
        var matched = new HashSet<ExistingFirewallRule>(ReferenceEqualityComparer.Instance);
        foreach (var rule in planned)
        {
            var match = existing.FirstOrDefault(e => e.Enabled && e.Allow && Matches(rule, e));
            if (match is null) return false;
            matched.Add(match);
        }

        return !existing.Any(e => !matched.Contains(e) && e.Enabled && IsOurs(e, paths) && (!e.Allow || (e.Profiles & Public) != 0));
    }

    private static bool Matches(FirewallRule rule, ExistingFirewallRule e) =>
        string.Equals(rule.Name, e.Name, StringComparison.OrdinalIgnoreCase)
        && string.Equals(rule.Program, e.Program, StringComparison.OrdinalIgnoreCase)
        && (int)rule.Protocol == e.Protocol
        && string.Equals(rule.LocalPorts, e.LocalPorts, StringComparison.OrdinalIgnoreCase)
        && rule.Profiles == e.Profiles
        && string.Equals(rule.RemoteAddresses, e.RemoteAddresses, StringComparison.OrdinalIgnoreCase);
}
