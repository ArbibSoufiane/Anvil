using System.Text.Json.Nodes;

namespace Anvil.Core.Sites;

/// <summary>
/// Reads a site file written by a build that still had IP sites, sub-paths, load balancers, passwords, Let's Encrypt and
/// client certificates. The file itself is never rewritten; only the copy in memory changes.
/// </summary>
public static class LegacySiteJson
{
    private static readonly string[] Gone = ["mounts", "trustedProxies", "acceptOnlyFromProxies", "allowedIps", "passwordProtected"];

    /// <summary>
    /// Brings <paramref name="site"/> to the current shape in place. Returns a warning when the site cannot be served any
    /// more and must be skipped, else null.
    /// </summary>
    public static string? Upgrade(JsonObject site, string fileName)
    {
        if (Text(site, "kind") == "Ip") return $"{fileName}: IP sites are no longer supported, so this site was skipped.";

        switch (Text(site, "access"))
        {
            case "Everyone":
                site["access"] = "Network";
                break;
            case "SpecificIps":
                site["access"] = "ThisPc";
                break;
        }

        if (Text(site, "cert") is "LetsEncrypt" or "Imported" or "LoadBalancer") site["cert"] = "None";
        foreach (var key in Gone) site.Remove(key);
        return null;
    }

    private static string? Text(JsonObject site, string key) =>
        site[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
