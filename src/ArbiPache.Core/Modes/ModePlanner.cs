using ArbiPache.Core.Database;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Settings;

namespace ArbiPache.Core.Modes;

/// <param name="PhpVersions">The PHP versions that run a pool: each gets its own mode file.</param>
/// <param name="HasXdebug">True when an Xdebug build is installed for the PHP version.</param>
/// <param name="Database">The active database engine, or null when there is none.</param>
public sealed record ModePlanInput(ModeProfile From, ModeProfile To, IReadOnlyList<string> PhpVersions, Func<string, bool> HasXdebug,
    DbEngine? Database);

/// <summary>Lists, file by file, every value that differs between two profiles.</summary>
public static class ModePlanner
{
    public static IReadOnlyList<SettingChange> Changes(ModePlanInput i, AppPaths paths)
    {
        var changes = new List<SettingChange>();
        string Data(string path) => Path.GetRelativePath(paths.Data, path);

        if (i.PhpVersions.Count > 0)
        {
            var more = i.PhpVersions.Count - 1;
            var file = Data(PhpModeIniRenderer.FilePath(paths, i.PhpVersions[0])) + (more == 0 ? "" : $" (+{more} more)");
            Diff(changes, "PHP", file, i.From.Php, i.To.Php);
            bool Loaded(ModeProfile p) => p.Xdebug && i.PhpVersions.Any(i.HasXdebug);
            if (Loaded(i.From) != Loaded(i.To))
                changes.Add(new SettingChange("PHP", file, "xdebug", Loaded(i.From) ? "loaded" : "not loaded", Loaded(i.To) ? "loaded" : "not loaded"));
        }

        const string backend = "PhpBackendTimeout";
        var modeConf = Data(Path.Combine(paths.Conf, "mode.conf"));
        var vhosts = Data(paths.Vhosts);
        Diff(changes, "Apache", modeConf, Without(i.From.Apache, backend), Without(i.To.Apache, backend));
        Diff(changes, "Apache", Data(Path.Combine(paths.Conf, "php-pools.conf")), Only(i.From.Apache, backend), Only(i.To.Apache, backend));
        if (i.From.SecurityHeaders != i.To.SecurityHeaders)
            changes.Add(new SettingChange("Apache", modeConf, "Security headers", Sent(i.From.SecurityHeaders), Sent(i.To.SecurityHeaders)));
        if (i.From.DirectoryListing != i.To.DirectoryListing)
            changes.Add(new SettingChange("Apache", vhosts, "Options Indexes (htdocs)", OnOff(i.From.DirectoryListing), OnOff(i.To.DirectoryListing)));
        if (i.From.Mode != i.To.Mode)
            changes.Add(new SettingChange("Apache", vhosts, "Unknown Host", CatchAll(i.From.Mode), CatchAll(i.To.Mode)));

        if (i.Database is { } e)
            Diff(changes, e == DbEngine.MariaDb ? "MariaDB" : "MySQL", Data(new MyIni(paths).ModeFile(e)),
                DbModeRenderer.Values(i.From, e), DbModeRenderer.Values(i.To, e));

        if (i.From.Mode != i.To.Mode)
            changes.Add(new SettingChange("Windows Firewall", "", "Web ports open to", Firewall(i.From.Mode), Firewall(i.To.Mode)));
        return changes;
    }

    private static void Diff(List<SettingChange> changes, string where, string file, IEnumerable<KeyValuePair<string, string>> from,
        IEnumerable<KeyValuePair<string, string>> to)
    {
        var before = from.ToDictionary(p => p.Key, p => p.Value, OptionNames.Comparer);
        var after = to.ToDictionary(p => p.Key, p => p.Value, OptionNames.Comparer);
        foreach (var key in after.Keys.Concat(before.Keys.Where(k => !after.ContainsKey(k))))
        {
            var a = before.GetValueOrDefault(key, "");
            var b = after.GetValueOrDefault(key, "");
            if (a != b) changes.Add(new SettingChange(where, file, key, a, b));
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> Without(IReadOnlyDictionary<string, string> d, string key) =>
        d.Where(p => !string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<KeyValuePair<string, string>> Only(IReadOnlyDictionary<string, string> d, string key) =>
        d.Where(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));

    private static string Sent(bool on) => on ? "sent" : "none";

    private static string OnOff(bool on) => on ? "On" : "Off";

    private static string CatchAll(ServerMode m) => m == ServerMode.Production ? "403, empty" : "htdocs (private ranges)";

    private static string Firewall(ServerMode m) => m == ServerMode.Production ? "The internet" : "Your network";
}
