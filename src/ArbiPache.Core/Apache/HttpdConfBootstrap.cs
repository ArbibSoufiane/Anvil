using System.Text.RegularExpressions;
using ArbiPache.Core.IO;
using ArbiPache.Core.Paths;

namespace ArbiPache.Core.Apache;

/// <summary>
/// data\apache\httpd.conf belongs to the user. ArbiPache touches only the <c>Define SRVROOT</c> value and one
/// marked block at the end that includes the generated configuration. When it first copies the stock file, it turns
/// off the stock access log, which would grow without end inside the Apache folder, and the stock Listen lines: the
/// ports come from Settings, and a stock Listen 80 would keep Apache on port 80 after the HTTP port moved.
/// </summary>
public sealed partial class HttpdConfBootstrap(AppPaths paths)
{
    private const string Begin = "# >>> ArbiPache >>>";
    private const string End = "# <<< ArbiPache <<<";

    public void EnsureUserConf(string apacheVersion)
    {
        if (!File.Exists(paths.HttpdConf))
        {
            Directory.CreateDirectory(paths.ApacheConfDir);
            var stock = File.ReadAllText(Path.Combine(paths.ApacheRuntime(apacheVersion), "conf", "httpd.conf"));
            stock = StockAccessLog().Replace(stock, @"$1#$2  # ArbiPache writes the access log to data\logs\apache, one file per day");
            AtomicFile.WriteAllText(paths.HttpdConf, StockListen().Replace(stock, "$1#$2  # ArbiPache listens on the ports set in Settings"));
        }
        var current = File.ReadAllText(paths.HttpdConf);
        var updated = ApplyManagedLines(current, paths.ApacheRuntime(apacheVersion), Path.Combine(paths.Conf, "arbipache.conf"));
        if (updated != current) AtomicFile.WriteAllText(paths.HttpdConf, updated);
    }

    public static string ApplyManagedLines(string conf, string srvRoot, string includeFile)
    {
        var nl = conf.Contains("\r\n") ? "\r\n" : "\n";
        conf = ManagedBlock().Replace(conf, "");

        var root = AppPaths.ForApache(srvRoot);
        conf = SrvRoot().IsMatch(conf)
            ? SrvRoot().Replace(conf, m => $"{m.Groups[1].Value}\"{root}\"", 1)
            : $"Define SRVROOT \"{root}\"{nl}{conf}";

        if (conf.Length > 0 && !conf.EndsWith('\n')) conf += nl;
        return $"{conf}{Begin}{nl}Include \"{AppPaths.ForApache(includeFile)}\"{nl}{End}{nl}";
    }

    [GeneratedRegex(@"^([ \t]*)(CustomLog[ \t]+""logs/access\.log""[ \t]+common)[ \t]*(?=\r?$)", RegexOptions.Multiline)]
    private static partial Regex StockAccessLog();

    [GeneratedRegex(@"^([ \t]*)(Listen[ \t]+[^\r\n#]*?)[ \t]*(?=\r?$)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex StockListen();

    [GeneratedRegex(@"^# >>> ArbiPache >>>\r?\n(?:.*\r?\n)*?# <<< ArbiPache <<<\r?(?:\n|\z)", RegexOptions.Multiline)]
    private static partial Regex ManagedBlock();

    [GeneratedRegex(@"^([ \t]*Define[ \t]+SRVROOT[ \t]+)(""[^""\r\n]*""|[^\s]+)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex SrvRoot();
}
