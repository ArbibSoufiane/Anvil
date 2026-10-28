using System.Text.RegularExpressions;
using Anvil.Core.IO;
using Anvil.Core.Paths;
using Anvil.Core.Settings;

namespace Anvil.Core.Database;

/// <summary>
/// data\&lt;engine&gt;\my.ini: the user's file. Anvil writes it once, with the development values, then only changes the
/// [mysqld] keys set on the Database page.
/// </summary>
public sealed partial class MyIni(AppPaths paths)
{
    private const string Server = "mysqld";

    // Older builds wrote the mode's values into mode.cnf beside my.ini and included it on my.ini's last line.
    private static readonly string[] OldModeMarkers = ["# Written by ArbiPache", "# Written by Anvil"]; // legacy-name

    [GeneratedRegex(@"^\s*!include\s+.*[/\\]mode\.cnf\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ModeInclude();

    public string PathFor(DbEngine e) => Path.Combine(paths.DbData(e), "my.ini");

    /// <summary>A folder of its own: MySQL refuses to initialize a datadir that already holds files such as my.ini.</summary>
    public string DataDir(DbEngine e) => Path.Combine(paths.DbData(e), "data");

    public string ErrorLog => Path.Combine(paths.Logs, "db", "error.log");

    public void EnsureCreated(DbEngine e, int port)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ErrorLog)!);
        var file = PathFor(e);
        if (File.Exists(file))
        {
            DropModeInclude(e);
            return;
        }
        var name = e == DbEngine.MariaDb ? "MariaDB" : "MySQL";
        var defaults = string.Concat(DevelopmentDefaults.Database(e).Select(p => $"{p.Key}={p.Value}\n"));
        // Option files read backslashes as escapes: paths use forward slashes, quoted because the root may contain spaces.
        AtomicFile.WriteAllText(file, $"""
            # {name} settings. This file is yours: Anvil only changes the keys you save on the Database page.
            [mysqld]
            datadir="{AppPaths.ForApache(DataDir(e))}"
            port={port}
            bind-address=127.0.0.1
            log-error="{AppPaths.ForApache(ErrorLog)}"
            character-set-server=utf8mb4

            """ + defaults);
    }

    /// <summary>
    /// Takes an older build's mode include out of my.ini. A mode.cnf that build generated is deleted and its development
    /// values move into my.ini, since the server ran with them; one the user wrote stays where it is.
    /// </summary>
    private void DropModeInclude(DbEngine e)
    {
        var file = PathFor(e);
        var text = File.ReadAllText(file);
        var lines = text.Split('\n');
        if (!lines.Any(l => ModeInclude().IsMatch(l.TrimEnd('\r')))) return;
        AtomicFile.WriteAllText(file, string.Join('\n', lines.Where(l => !ModeInclude().IsMatch(l.TrimEnd('\r')))));

        var mode = Path.Combine(paths.DbData(e), "mode.cnf");
        if (!File.Exists(mode) || !OldModeMarkers.Any((File.ReadLines(mode).FirstOrDefault() ?? "").StartsWith)) return;
        File.Delete(mode);
        Set(e, DevelopmentDefaults.Database(e).ToDictionary(p => p.Key, p => (string?)p.Value));
    }

    /// <summary>
    /// After the folder moved: every path in my.ini inside <paramref name="oldRoot"/> (datadir, log-error, anything the
    /// user pointed into the folder) now names the same place inside this root. Paths elsewhere stay.
    /// </summary>
    public void Rebase(DbEngine e, string oldRoot)
    {
        var file = PathFor(e);
        if (!File.Exists(file)) return;
        var old = AppPaths.ForApache(Path.TrimEndingDirectorySeparator(oldRoot.Replace('/', '\\')));
        var text = File.ReadAllText(file);
        // Only where a path starts: after a quote, '=', whitespace or the line start, and followed by a slash.
        var rebased = Regex.Replace(text, $@"(?<=^|[""'=\s]){Regex.Escape(old)}(?=/)", _ => AppPaths.ForApache(paths.Root),
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (rebased != text) AtomicFile.WriteAllText(file, rebased);
    }

    /// <summary>The [mysqld] values; "max-connections" and "max_connections" find the same key.</summary>
    public IReadOnlyDictionary<string, string> Read(DbEngine e)
    {
        var values = new Dictionary<string, string>(OptionNames.Comparer);
        foreach (var (key, value) in IniDocument.Parse(File.ReadAllText(PathFor(e))).Pairs(Server)) values[key] = value;
        return values;
    }

    /// <summary>Sets [mysqld] keys in place, keeping the user's spelling and comments. A null value removes the key.</summary>
    public void Set(DbEngine e, IReadOnlyDictionary<string, string?> changes)
    {
        var file = PathFor(e);
        var text = File.ReadAllText(file);
        var doc = IniDocument.Parse(text);

        foreach (var (key, value) in changes)
        {
            var spellings = doc.Pairs(Server).Select(p => p.Key).Where(k => OptionNames.Comparer.Equals(k, key)).Distinct().ToList();
            var keep = value is null ? null : spellings.LastOrDefault() ?? key;
            foreach (var spelling in spellings.Where(s => s != keep)) doc.Remove(Server, spelling);
            if (keep is not null) doc.Set(Server, keep, value!);
        }

        AtomicFile.WriteAllText(file, doc.ToString());
    }
}
