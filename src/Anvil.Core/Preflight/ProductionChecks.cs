using System.Net;
using Anvil.Core.Modes;
using Anvil.Core.Settings;
using Anvil.Core.Sites;

namespace Anvil.Core.Preflight;

/// <summary>
/// In Production, what would leave the server weaker than it looks: a database reachable from other computers and PHP
/// errors shown to visitors.
/// </summary>
public static class ProductionChecks
{
    private static readonly string[] DisplayedErrors = ["on", "1", "true", "yes", "stdout"];
    private static readonly string[] Off = ["0", "off", "false", "no"];

    /// <param name="production">The Production profile with the user's overrides.</param>
    /// <param name="dbIni">The active engine's [mysqld] values, or null when no database is installed.</param>
    /// <param name="looksLikePhpMyAdmin">True when a site folder holds phpMyAdmin; see <see cref="LooksLikePhpMyAdmin"/>.</param>
    public static IReadOnlyList<PreflightFinding> Run(AppSettings s, ModeProfile production,
        IReadOnlyList<SiteDefinition> sites, IReadOnlyDictionary<string, string>? dbIni, Func<string, bool> looksLikePhpMyAdmin)
    {
        if (s.Mode != ServerMode.Production) return [];
        var findings = new List<PreflightFinding>();

        if (dbIni is not null && ExposedBind(dbIni) is { } bind)
            findings.Add(new("prod-db-exposed", Severity.Warning,
                bind.Length == 0
                    ? "The database accepts connections from other computers: my.ini has no bind-address."
                    : $"The database accepts connections from other computers: bind-address is {bind}.",
                "Set bind-address to 127.0.0.1 on the Database page, unless another server must reach the database."));

        if (production.Php.TryGetValue("display_errors", out var display) && DisplayedErrors.Contains(Unquote(display), StringComparer.OrdinalIgnoreCase))
            findings.Add(new("prod-display-errors", Severity.Warning,
                $"The Production profile shows PHP errors to visitors (display_errors = {display}).",
                "Set display_errors back to Off on the Dev / Production page."));

        return findings;
    }

    /// <summary>A phpMyAdmin folder: <c>libraries\classes\</c> and <c>config.sample.inc.php</c> side by side.</summary>
    public static bool LooksLikePhpMyAdmin(string root) =>
        Directory.Exists(Path.Combine(root, "libraries", "classes")) && File.Exists(Path.Combine(root, "config.sample.inc.php"));

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
