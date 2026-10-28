namespace Anvil.Core.Hosts;

/// <summary>
/// Edits Anvil's own block in the Windows hosts file and nothing else. Each domain maps to 127.0.0.1 and ::1.
/// The file's line endings are kept; an empty domain list removes the block. Lines ArbiPache 3 wrote for a domain this
/// block now holds are dropped from ArbiPache 3's block, and that block goes once it is empty.
/// </summary>
public static class HostsFile
{
    public const string BeginMarker = "# >>> Anvil >>>";
    public const string EndMarker = "# <<< Anvil <<<";
    public const string LegacyBeginMarker = "# >>> ApacheManager >>>";
    public const string LegacyEndMarker = "# <<< ApacheManager <<<";

    // Not Split(' ', '\t', options): that binds to Split(char, int count, options).
    private static readonly char[] Blanks = [' ', '\t'];

    public static string ApplyBlock(string hosts, IReadOnlyList<string> domains)
    {
        var newline = hosts.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : hosts.Contains('\n') ? "\n" : "\r\n";
        var lines = hosts.Length == 0 ? [] : hosts.Split(newline).ToList();
        var endsWithNewline = lines.Count > 0 && lines[^1].Length == 0;
        if (endsWithNewline) lines.RemoveAt(lines.Count - 1);
        var unique = domains.Select(d => d.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();

        var (legacyBegin, legacyEnd) = Find(lines, LegacyBeginMarker, LegacyEndMarker);
        if (legacyEnd >= 0)
        {
            var kept = lines.GetRange(legacyBegin + 1, legacyEnd - legacyBegin - 1)
                .Where(l => DomainOf(l) is not { } d || !unique.Contains(d)).ToList();
            List<string> block = kept.Any(l => DomainOf(l) is not null) ? [lines[legacyBegin], .. kept, lines[legacyEnd]] : [];
            lines.RemoveRange(legacyBegin, legacyEnd - legacyBegin + 1);
            lines.InsertRange(legacyBegin, block);
        }

        var (begin, end) = Find(lines, BeginMarker, EndMarker);
        var at = lines.Count;
        if (end >= 0)
        {
            lines.RemoveRange(begin, end - begin + 1);
            at = begin;
        }

        if (unique.Count > 0)
        {
            lines.InsertRange(at, [BeginMarker, .. unique.SelectMany(d => new[] { $"127.0.0.1 {d}", $"::1 {d}" }), EndMarker]);
            endsWithNewline = true;
        }

        return lines.Count == 0 ? "" : string.Join(newline, lines) + (endsWithNewline ? newline : "");
    }

    public static IReadOnlyList<string> ManagedDomains(string hosts) => DomainsBetween(hosts, BeginMarker, EndMarker);

    /// <summary>The domains still in ArbiPache 3's block.</summary>
    public static IReadOnlyList<string> LegacyDomains(string hosts) => DomainsBetween(hosts, LegacyBeginMarker, LegacyEndMarker);

    private static (int Begin, int End) Find(List<string> lines, string beginMarker, string endMarker)
    {
        var begin = lines.FindIndex(l => l.Trim() == beginMarker);
        return (begin, begin < 0 ? -1 : lines.FindIndex(begin, l => l.Trim() == endMarker));
    }

    private static string? DomainOf(string line) =>
        line.Split(Blanks, StringSplitOptions.RemoveEmptyEntries) is [var address, var domain, ..] && !address.StartsWith('#')
            ? domain.ToLowerInvariant()
            : null;

    private static IReadOnlyList<string> DomainsBetween(string hosts, string beginMarker, string endMarker)
    {
        var domains = new List<string>();
        var inside = false;
        foreach (var raw in hosts.Split('\n'))
        {
            var line = raw.Trim();
            if (line == beginMarker) inside = true;
            else if (line == endMarker) inside = false;
            else if (inside && line.Split(Blanks, StringSplitOptions.RemoveEmptyEntries) is ["127.0.0.1", var domain, ..] && !domains.Contains(domain))
                domains.Add(domain);
        }

        return domains;
    }
}
