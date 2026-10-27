using System.Text;
using ArbiPache.Core.Components;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Php;

namespace ArbiPache.Core.Modes;

/// <summary>
/// data\php\&lt;v&gt;\conf.d\99-arbipache-mode.ini: the mode's PHP values and, in a mode that wants it, Xdebug. PHP loads it
/// after php.ini (PHP_INI_SCAN_DIR), so these values win while php.ini stays as the user left it.
/// </summary>
public static class PhpModeIniRenderer
{
    public const string FileName = "99-arbipache-mode.ini";

    public static string FilePath(AppPaths paths, string version) => Path.Combine(paths.PhpConfigDir(version), "conf.d", FileName);

    public static string Render(ModeProfile p, string version, string? xdebugDll)
    {
        var text = new StringBuilder()
            .Append($"; Written by ArbiPache for {p.Mode} mode (PHP {version}). Loaded after php.ini, so these values win.\r\n")
            .Append("; Change them on the Dev / Production page: this file is rewritten.\r\n")
            .Append("[PHP]\r\n");
        foreach (var (key, value) in p.Php)
        {
            if ((key + value).Any(char.IsControl))
                throw new ArgumentException($"The {p.Mode} profile's PHP value for {key} cannot contain line breaks.");
            text.Append($"{key} = {value}".TrimEnd()).Append("\r\n");
        }
        if (p.Xdebug && xdebugDll is not null)
        {
            text.Append("\r\n[xdebug]\r\n")
                .Append($"zend_extension=\"{AppPaths.ForApache(xdebugDll)}\"\r\n")
                .Append("xdebug.mode = debug,develop\r\n")
                .Append("xdebug.start_with_request = trigger\r\n");
        }
        return text.ToString();
    }

    /// <summary>The DLL of the installed Xdebug build for this PHP version's minor, or null when there is none.</summary>
    public static string? XdebugDll(IEnumerable<InstalledComponent> installed, string phpVersion)
    {
        var minor = PhpSupport.Minor(phpVersion);
        var build = installed.FirstOrDefault(i => i.Kind == ComponentKind.Xdebug && i.AppliesTo == minor);
        if (build is null || !Directory.Exists(build.Path)) return null;
        return Directory.GetFiles(build.Path, "*.dll").Order(StringComparer.Ordinal).FirstOrDefault();
    }
}
