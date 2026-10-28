using Anvil.Core.Components;
using Anvil.Core.Paths;

namespace Anvil.Core.Logs;

/// <param name="Id">apache, php-&lt;version&gt;, db, site-&lt;id&gt; or anvil.</param>
public sealed record LogSource(string Id, string Label, string Path);

public static class LogSources
{
    /// <summary>
    /// Every log under data\logs: Apache's error log (listed even before it exists), one per PHP version, the database,
    /// one per site and Anvil's own Agent log. A rotated log is represented by its newest file.
    /// </summary>
    public static IReadOnlyList<LogSource> Discover(AppPaths paths)
    {
        var logs = paths.Logs;
        var sources = new List<LogSource>
        {
            new("apache", "Apache", Current(Path.Combine(logs, "apache"), "error") ?? Path.Combine(logs, "apache", "error.log")),
        };

        var php = Path.Combine(logs, "php");
        if (Directory.Exists(php))
            foreach (var version in Directory.EnumerateFiles(php, "*.log").Select(f => Path.GetFileNameWithoutExtension(f))
                         .OrderByDescending(v => v, Comparer<string>.Create(SemVer.Compare)))
                sources.Add(new LogSource($"php-{version}", $"PHP {version}", Path.Combine(php, version + ".log")));

        if (Current(Path.Combine(logs, "db"), "error") is { } db) sources.Add(new LogSource("db", "Database", db));

        var sites = Path.Combine(logs, "sites");
        if (Directory.Exists(sites))
            foreach (var dir in Directory.EnumerateDirectories(sites).Order(StringComparer.OrdinalIgnoreCase))
                if (Current(dir, "error") is { } site)
                {
                    var id = Path.GetFileName(dir);
                    sources.Add(new LogSource($"site-{id}", id, site));
                }

        if (Newest(Path.Combine(logs, "anvil"), "agent-*.log") is { } agent) sources.Add(new LogSource("anvil", "Anvil", agent));
        return sources;
    }

    // The newest "<name>.<date>.log" rotatelogs writes, unless an older single "<name>.log" was written more recently.
    private static string? Current(string dir, string name)
    {
        var plain = Path.Combine(dir, name + ".log");
        var dated = Newest(dir, name + ".*.log");
        if (!File.Exists(plain)) return dated;
        return dated is not null && File.GetLastWriteTimeUtc(dated) >= File.GetLastWriteTimeUtc(plain) ? dated : plain;
    }

    // Dated names (2026-10-27, 20261027) sort the same as their dates.
    private static string? Newest(string dir, string pattern) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern).Order(StringComparer.OrdinalIgnoreCase).LastOrDefault() : null;
}
