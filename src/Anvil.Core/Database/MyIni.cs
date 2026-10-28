using System.Text.RegularExpressions;
using Anvil.Core.IO;
using Anvil.Core.Paths;

namespace Anvil.Core.Database;

/// <summary>
/// data\&lt;engine&gt;\my.ini: the user's file. Anvil writes it once, then only changes the [mysqld] keys set on the
/// Database page. Its last line includes mode.cnf beside it, so Development and Production settings win.
/// </summary>
public sealed class MyIni(AppPaths paths)
{
    private const string Server = "mysqld";

    public string PathFor(DbEngine e) => Path.Combine(paths.DbData(e), "my.ini");

    /// <summary>A folder of its own: MySQL refuses to initialize a datadir that already holds files such as my.ini.</summary>
    public string DataDir(DbEngine e) => Path.Combine(paths.DbData(e), "data");

    /// <summary>
    /// The mode's values for the engine. Not in data\conf: the Apache config writer replaces that folder as a whole.
    /// </summary>
    public string ModeFile(DbEngine e) => Path.Combine(paths.DbData(e), "mode.cnf");

    public string ErrorLog => Path.Combine(paths.Logs, "db", "error.log");

    private string Include(DbEngine e) => $"!include {AppPaths.ForApache(ModeFile(e))}";

    public void EnsureCreated(DbEngine e, int port)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ErrorLog)!);
        // The server refuses to start when an included file is missing.
        if (!File.Exists(ModeFile(e))) AtomicFile.WriteAllText(ModeFile(e), "[mysqld]\n");

        var file = PathFor(e);
        if (File.Exists(file)) return;
        var name = e == DbEngine.MariaDb ? "MariaDB" : "MySQL";
        // Option files read backslashes as escapes: paths use forward slashes, quoted because the root may contain spaces.
        AtomicFile.WriteAllText(file, $"""
            # {name} settings. This file is yours: Anvil only changes the keys you save on the Database page.
            # Development and Production mode settings come from the file included on the last line.
            [mysqld]
            datadir="{AppPaths.ForApache(DataDir(e))}"
            port={port}
            bind-address=127.0.0.1
            log-error="{AppPaths.ForApache(ErrorLog)}"
            character-set-server=utf8mb4
            {Include(e)}

            """);
    }

    /// <summary>
    /// After the folder moved: every path in my.ini inside <paramref name="oldRoot"/> (datadir, log-error, the mode include,
    /// anything the user pointed into the folder) now names the same place inside this root. Paths elsewhere stay.
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
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        // Lift the mode include out so new keys land above it, then put it back as the last line.
        var withoutInclude = string.Join('\n', text.Split('\n').Where(l => !string.Equals(l.Trim(), Include(e), StringComparison.OrdinalIgnoreCase)));
        var doc = IniDocument.Parse(withoutInclude);

        foreach (var (key, value) in changes)
        {
            var spellings = doc.Pairs(Server).Select(p => p.Key).Where(k => OptionNames.Comparer.Equals(k, key)).Distinct().ToList();
            var keep = value is null ? null : spellings.LastOrDefault() ?? key;
            foreach (var spelling in spellings.Where(s => s != keep)) doc.Remove(Server, spelling);
            if (keep is not null) doc.Set(Server, keep, value!);
        }

        var result = doc.ToString().TrimEnd('\r', '\n');
        AtomicFile.WriteAllText(file, result + newline + Include(e) + newline);
    }
}
