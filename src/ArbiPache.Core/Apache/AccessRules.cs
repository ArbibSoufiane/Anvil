using ArbiPache.Core.Paths;
using ArbiPache.Core.Sites;

namespace ArbiPache.Core.Apache;

/// <summary>
/// The Require block for a site's &lt;Directory&gt;, unindented, one directive per line ("\n"). Access level picks who may
/// connect; a password and accept-only-from-proxies are added on top of it, never instead of it.
/// </summary>
public static class AccessRules
{
    private const string Indent = "    ";

    public static string Render(SiteDefinition s, string? htpasswdPath)
    {
        if (s.PasswordProtected && htpasswdPath is null)
            throw new ArgumentException($"{s.Name} is password protected but has no password file.", nameof(htpasswdPath));

        var who = s.Access switch
        {
            AccessLevel.Everyone => ["Require all granted"],
            AccessLevel.Network => [$"Require ip {ConfigRenderer.PrivateRanges}"],
            AccessLevel.ThisPc => ["Require local"],
            AccessLevel.SpecificIps => (List<string>)["Require local", $"Require ip {string.Join(' ', s.AllowedIps)}"],
            _ => throw new ArgumentOutOfRangeException(nameof(s), s.Access, null),
        };

        var also = new List<string>();
        if (s.AcceptOnlyFromProxies && s.TrustedProxies.Count > 0)
            also.Add($"Require expr \"{string.Join(" || ", s.TrustedProxies.Select(p => $"%{{CONN_REMOTE_ADDR}} -ipmatch '{p}'"))}\"");
        if (s.PasswordProtected) also.Add("Require valid-user");

        var lines = new List<string>();
        if (s.PasswordProtected)
        {
            lines.Add("AuthType Basic");
            lines.Add($"AuthName \"{s.Name}\"");
            lines.Add("AuthBasicProvider file");
            lines.Add($"AuthUserFile \"{AppPaths.ForApache(htpasswdPath!)}\"");
        }

        if (also.Count == 0)
        {
            lines.AddRange(who);
        }
        else
        {
            lines.Add("<RequireAll>");
            lines.Add(Indent + "<RequireAny>");
            lines.AddRange(who.Select(w => Indent + Indent + w));
            lines.Add(Indent + "</RequireAny>");
            lines.AddRange(also.Select(a => Indent + a));
            lines.Add("</RequireAll>");
        }

        return string.Join('\n', lines);
    }
}
