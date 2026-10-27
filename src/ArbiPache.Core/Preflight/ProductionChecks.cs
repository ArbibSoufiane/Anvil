using System.Net;
using ArbiPache.Core.Modes;
using ArbiPache.Core.Settings;
using ArbiPache.Core.Sites;

namespace ArbiPache.Core.Preflight;

/// <summary>
/// In Production, what would leave the server weaker than it looks: an Agent that stops at sign-out, a database or
/// phpMyAdmin reachable from the internet, PHP errors shown to visitors, and public sites without trusted HTTPS.
/// Certificate expiry is reported in both modes by the renewal planner.
/// </summary>
public static class ProductionChecks
{
    private static readonly string[] DisplayedErrors = ["on", "1", "true", "yes", "stdout"];
    private static readonly string[] Off = ["0", "off", "false", "no"];

    /// <param name="runningAsService">The Agent runs as the Windows service now, not only in the settings.</param>
    /// <param name="production">The Production profile with the user's overrides.</param>
    /// <param name="dbIni">The active engine's [mysqld] values, or null when no database is installed.</param>
    /// <param name="looksLikePhpMyAdmin">True when a site folder holds phpMyAdmin; see <see cref="LooksLikePhpMyAdmin"/>.</param>
    public static IReadOnlyList<PreflightFinding> Run(AppSettings s, bool runningAsService, ModeProfile production,
        IReadOnlyList<SiteDefinition> sites, IReadOnlyDictionary<string, string>? dbIni, Func<string, bool> looksLikePhpMyAdmin)
    {
        if (s.Mode != ServerMode.Production) return [];
        var findings = new List<PreflightFinding>();

        if (!runningAsService)
            findings.Add(new("prod-not-service", Severity.Warning,
                "ArbiPache is not running as a Windows service, so sites stop when you sign out or Windows restarts.",
                "Turn on \"Run as a Windows service\" in Settings."));

        if (dbIni is not null && ExposedBind(dbIni) is { } bind)
            findings.Add(new("prod-db-exposed", Severity.Warning,
                bind.Length == 0
                    ? "The database accepts connections from other computers: my.ini has no bind-address."
                    : $"The database accepts connections from other computers: bind-address is {bind}.",
                "Set bind-address to 127.0.0.1 on the Database page, unless another server must reach the database."));

        var open = sites.Where(site => site.Enabled && site.Access == AccessLevel.Everyone).ToList();

        var phpMyAdmin = open.Where(site => !site.PasswordProtected).SelectMany(site => PhpMyAdminUrls(site, looksLikePhpMyAdmin)).ToList();
        if (phpMyAdmin.Count > 0)
            findings.Add(new("prod-pma-public", Severity.Warning,
                $"phpMyAdmin is open to everyone on the internet at {string.Join(", ", phpMyAdmin)}.",
                "In the site's settings, set \"Who can open it\" to My network only, This PC only or Specific IPs, or ask for a username and password."));

        if (production.Php.TryGetValue("display_errors", out var display) && DisplayedErrors.Contains(Unquote(display), StringComparer.OrdinalIgnoreCase))
            findings.Add(new("prod-display-errors", Severity.Warning,
                $"The Production profile shows PHP errors to visitors (display_errors = {display}).",
                "Set display_errors back to Off on the Dev / Production page."));

        var plainHttp = open.Where(site => site.Cert is CertSource.None or CertSource.LocalDev).Select(site => site.Name).ToList();
        if (plainHttp.Count > 0)
            findings.Add(new("prod-no-https", Severity.Warning,
                $"{string.Join(", ", plainHttp)} {(plainHttp.Count == 1 ? "is" : "are")} open to everyone without a trusted HTTPS certificate.",
                "In the site's settings, use Let's Encrypt, the certificate the client sent, or HTTP only behind the client's load balancer."));

        return findings;
    }

    /// <summary>A phpMyAdmin folder: <c>libraries\classes\</c> and <c>config.sample.inc.php</c> side by side.</summary>
    public static bool LooksLikePhpMyAdmin(string root) =>
        Directory.Exists(Path.Combine(root, "libraries", "classes")) && File.Exists(Path.Combine(root, "config.sample.inc.php"));

    private static IEnumerable<string> PhpMyAdminUrls(SiteDefinition site, Func<string, bool> looksLikePhpMyAdmin)
    {
        if (site.Type != SiteType.Proxy && looksLikePhpMyAdmin(site.Root)) yield return site.Name;
        foreach (var mount in site.Mounts.Where(m => m.Type != SiteType.Proxy && looksLikePhpMyAdmin(m.Root)))
            yield return site.Name + mount.UrlPath;
    }

    // Null when only this PC can connect; "" when my.ini leaves the server listening on every address.
    private static string? ExposedBind(IReadOnlyDictionary<string, string> ini)
    {
        if (ini.TryGetValue("skip-networking", out var skip) && !Off.Contains(Unquote(skip), StringComparer.OrdinalIgnoreCase)) return null;
        if (!ini.TryGetValue("bind-address", out var bind)) return "";
        var addresses = Unquote(bind).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return addresses.Length > 0 && addresses.All(IsLoopback) ? null : bind;
    }

    private static bool IsLoopback(string address) =>
        string.Equals(address, "localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(address, out var ip) && IPAddress.IsLoopback(ip));

    private static string Unquote(string value) => value.Trim().Trim('"', '\'').Trim();
}
