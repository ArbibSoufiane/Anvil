using System.Text.RegularExpressions;

namespace Anvil.Core.Apache;

/// <param name="LoadedModules">Module names without the "_module" suffix, e.g. "ssl".</param>
public sealed record StockConfState(IReadOnlySet<string> LoadedModules, IReadOnlySet<int> ListenPorts);

/// <summary>Reads which modules and ports the user's httpd.conf already provides, so the generated config does not repeat them.</summary>
public static partial class StockConfScanner
{
    public static StockConfState Scan(string httpdConf)
    {
        var modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in LoadModule().Matches(httpdConf))
        {
            var name = m.Groups[1].Value;
            modules.Add(name.EndsWith("_module", StringComparison.OrdinalIgnoreCase) ? name[..^"_module".Length] : name);
        }

        var ports = new HashSet<int>();
        foreach (Match m in Listen().Matches(httpdConf))
            if (int.TryParse(m.Groups[1].Value, out var port)) ports.Add(port);

        return new StockConfState(modules, ports);
    }

    [GeneratedRegex(@"^[ \t]*LoadModule[ \t]+(\S+)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex LoadModule();

    // "Listen 80", "Listen 0.0.0.0:80", "Listen [::]:80 http": the port is the last colon-separated part of the address.
    [GeneratedRegex(@"^[ \t]*Listen[ \t]+(?:\S*:)?(\d+)\b", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex Listen();
}
