using System.Diagnostics;

namespace ArbiPache.Core.Database;

/// <summary>
/// Reads a server's built-in defaults from "--no-defaults --verbose --help", so the Database page shows what the installed
/// version really uses rather than numbers typed into ArbiPache.
/// </summary>
public static class DefaultsProbe
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    /// <summary>Runs mariadbd.exe or mysqld.exe without any option file and parses its variables table.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> ReadAsync(string serverExe, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(serverExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(serverExe)!,
        };
        // --no-defaults must come first, or the server reads my.ini files from the usual places.
        psi.ArgumentList.Add("--no-defaults");
        psi.ArgumentList.Add("--verbose");
        psi.ArgumentList.Add("--help");

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Limit);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{Path.GetFileName(serverExe)} --verbose --help did not finish within {Limit.TotalSeconds:0} seconds.");
        }

        var defaults = Parse(await stdout);
        if (defaults.Count == 0)
            throw new InvalidOperationException($"{Path.GetFileName(serverExe)} did not list its defaults. {(await stderr).Trim()}");
        return defaults;
    }

    /// <summary>
    /// The "Variables (--variable-name=value)" table: one option per line, name then value, up to the first blank line.
    /// Lookups accept dashes or underscores ("max-connections" and "max_connections" are the same key).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parse(string helpOutput)
    {
        var defaults = new Dictionary<string, string>(OptionNameComparer.Instance);
        var lines = helpOutput.ReplaceLineEndings("\n").Split('\n');
        var start = Array.FindIndex(lines, l => l.StartsWith("Variables (--variable-name=value)", StringComparison.Ordinal));
        if (start < 0) return defaults;

        // The header spans two lines and ends with a row of dashes.
        var row = Array.FindIndex(lines, start, l => l.StartsWith("---", StringComparison.Ordinal)) + 1;
        if (row == 0) return defaults;
        for (; row < lines.Length && lines[row].Trim().Length > 0; row++)
        {
            var line = lines[row].Trim();
            var gap = line.IndexOfAny([' ', '\t']);
            var value = gap < 0 ? "" : line[gap..].Trim();
            // MariaDB writes "(No default value)" where MySQL leaves the column empty.
            defaults[gap < 0 ? line : line[..gap]] = value == "(No default value)" ? "" : value;
        }
        return defaults;
    }

    private sealed class OptionNameComparer : IEqualityComparer<string>
    {
        public static readonly OptionNameComparer Instance = new();

        public bool Equals(string? x, string? y) =>
            string.Equals(x?.Replace('-', '_'), y?.Replace('-', '_'), StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(string obj) => StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Replace('-', '_'));
    }
}
