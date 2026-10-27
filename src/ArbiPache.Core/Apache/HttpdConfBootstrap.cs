using System.Text.RegularExpressions;
using ArbiPache.Core.IO;
using ArbiPache.Core.Paths;

namespace ArbiPache.Core.Apache;

/// <summary>
/// data\apache\httpd.conf belongs to the user. ArbiPache touches only the <c>Define SRVROOT</c> value and one
/// marked block at the end that includes the generated configuration.
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
            File.Copy(Path.Combine(paths.ApacheRuntime(apacheVersion), "conf", "httpd.conf"), paths.HttpdConf);
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

    [GeneratedRegex(@"^# >>> ArbiPache >>>\r?\n(?:.*\r?\n)*?# <<< ArbiPache <<<\r?(?:\n|\z)", RegexOptions.Multiline)]
    private static partial Regex ManagedBlock();

    [GeneratedRegex(@"^([ \t]*Define[ \t]+SRVROOT[ \t]+)(""[^""\r\n]*""|[^\s]+)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex SrvRoot();
}
