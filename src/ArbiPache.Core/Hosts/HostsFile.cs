namespace ArbiPache.Core.Hosts;

/// <summary>
/// Edits ArbiPache's own block in the Windows hosts file and nothing else. Each domain maps to 127.0.0.1 and ::1.
/// The file's line endings are kept; an empty domain list removes the block.
/// </summary>
public static class HostsFile
{
    public const string BeginMarker = "# >>> ArbiPache >>>";
    public const string EndMarker = "# <<< ArbiPache <<<";

    public static string ApplyBlock(string hosts, IReadOnlyList<string> domains)
    {
        var newline = hosts.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : hosts.Contains('\n') ? "\n" : "\r\n";
        var lines = hosts.Length == 0 ? [] : hosts.Split(newline).ToList();
        var endsWithNewline = lines.Count > 0 && lines[^1].Length == 0;
        if (endsWithNewline) lines.RemoveAt(lines.Count - 1);

        var begin = lines.FindIndex(l => l.Trim() == BeginMarker);
        var end = begin < 0 ? -1 : lines.FindIndex(begin, l => l.Trim() == EndMarker);
        var at = lines.Count;
        if (begin >= 0 && end >= 0)
        {
            lines.RemoveRange(begin, end - begin + 1);
            at = begin;
        }

        var unique = domains.Select(d => d.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
        if (unique.Count > 0)
        {
            lines.InsertRange(at, [BeginMarker, .. unique.SelectMany(d => new[] { $"127.0.0.1 {d}", $"::1 {d}" }), EndMarker]);
            endsWithNewline = true;
        }

        return lines.Count == 0 ? "" : string.Join(newline, lines) + (endsWithNewline ? newline : "");
    }

    public static IReadOnlyList<string> ManagedDomains(string hosts)
    {
        var domains = new List<string>();
        var inside = false;
        foreach (var raw in hosts.Split('\n'))
        {
            var line = raw.Trim();
            if (line == BeginMarker) inside = true;
            else if (line == EndMarker) inside = false;
            else if (inside && line.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries) is ["127.0.0.1", var domain, ..] && !domains.Contains(domain))
                domains.Add(domain);
        }

        return domains;
    }
}
