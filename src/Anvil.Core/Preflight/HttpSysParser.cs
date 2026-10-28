using System.Text.RegularExpressions;

namespace Anvil.Core.Preflight;

/// <summary>
/// Reads the output of <c>netsh http show servicestate view=requestq</c>. Only the URL lines are used, since the
/// labels around them are translated on non-English Windows while the URLs are not.
/// </summary>
public static partial class HttpSysParser
{
    [GeneratedRegex(@"^\s*(?<url>https?://(?:\[[^\]\s]+\]|[^:/\s]+):(?<port>\d{1,5})/\S*)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex UrlLine();

    /// <summary>The registered URLs whose port is <paramref name="port"/>, in output order, without duplicates.</summary>
    public static IReadOnlyList<string> RegistrationsOnPort(string netshOutput, int port) =>
        UrlLine().Matches(netshOutput)
            .Where(m => int.Parse(m.Groups["port"].ValueSpan) == port)
            .Select(m => m.Groups["url"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
