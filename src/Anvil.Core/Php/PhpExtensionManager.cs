using Anvil.Core.Components;
using Anvil.Core.IO;
using Anvil.Core.Paths;

namespace Anvil.Core.Php;

/// <param name="Available">ext\php_&lt;name&gt;.dll exists in the version's runtime, or for Xdebug, a build for its branch.</param>
public sealed record PhpExtension(string Name, bool Enabled, bool Available, bool IsZend);

/// <summary>
/// Turns extensions on and off in data\php\&lt;v&gt;\php.ini, keeping the rest of the file as the user left it. Xdebug installed
/// from the catalog lives in runtime\xdebug and loads from an [xdebug] section; <paramref name="xdebugBuilds"/> are those builds.
/// </summary>
/// <param name="phpFolder">Where a version's files are: runtime\php\&lt;v&gt; unless it is a linked folder such as pvm's.</param>
public sealed class PhpExtensionManager(AppPaths paths, IReadOnlyList<InstalledComponent>? xdebugBuilds = null, Func<string, string>? phpFolder = null)
{
    private const string Xdebug = "xdebug";
    private static readonly HashSet<string> ZendExtensions = new(StringComparer.OrdinalIgnoreCase) { "opcache", Xdebug };

    /// <summary>The DLL of the installed Xdebug build for this PHP version's minor, or null when there is none.</summary>
    public static string? XdebugDll(IEnumerable<InstalledComponent> installed, string phpVersion)
    {
        var minor = PhpSupport.Minor(phpVersion);
        var build = installed.FirstOrDefault(i => i.Kind == ComponentKind.Xdebug && i.AppliesTo == minor);
        if (build is null || !Directory.Exists(build.Path)) return null;
        return Directory.GetFiles(build.Path, "*.dll").Order(StringComparer.Ordinal).FirstOrDefault();
    }

    public IReadOnlyList<PhpExtension> List(string version)
    {
        var ext = Path.Combine(Folder(version), "ext");
        if (!Directory.Exists(ext)) return [];
        var ini = Load(version);
        var names = Directory.GetFiles(ext, "php_*.dll").Select(f => Path.GetFileNameWithoutExtension(f)["php_".Length..].ToLowerInvariant());
        // A catalog build of Xdebug shows even without a DLL in ext, and so does one php.ini still loads.
        var xdebugElsewhere = XdebugDll(xdebugBuilds ?? [], version) is not null || LoadsXdebugSection(ini);
        return (xdebugElsewhere ? names.Append(Xdebug).Distinct() : names)
            .Order(StringComparer.Ordinal)
            .Select(name => new PhpExtension(name, Enabled(ini, name), Available: true, ZendExtensions.Contains(name)))
            .ToList();
    }

    public void Set(string version, string name, bool enabled)
    {
        var file = IniFile(version);
        var ini = IniDocument.Parse(File.ReadAllText(file));
        var isXdebug = string.Equals(name, Xdebug, StringComparison.OrdinalIgnoreCase);
        if (isXdebug && enabled && XdebugDll(xdebugBuilds ?? [], version) is { } dll)
        {
            ini.Remove(Xdebug, "zend_extension");
            ini.AddValue(Xdebug, "zend_extension", $"\"{AppPaths.ForApache(dll)}\"");
            ini.Set(Xdebug, "xdebug.mode", "debug,develop");
            ini.Set(Xdebug, "xdebug.start_with_request", "trigger");
            AtomicFile.WriteAllText(file, ini.ToString());
            return;
        }

        var dllName = $"php_{name}.dll";
        if (enabled && !File.Exists(Path.Combine(Folder(version), "ext", dllName)))
            throw new ArgumentException(isXdebug
                ? $"No Xdebug build is installed for PHP {PhpSupport.Minor(version)}. Install it from the PHP page first."
                : $"PHP {version} has no {dllName} in its ext folder.");

        var key = Key(name);
        var current = ini.GetAll("PHP", key).Where(v => Normalize(v) == name.ToLowerInvariant()).ToList();
        if (enabled && current.Count == 0) ini.AddValue("PHP", key, name);
        if (!enabled) foreach (var value in current) ini.RemoveValue("PHP", key, value);
        if (isXdebug && !enabled)
            foreach (var setting in new[] { "zend_extension", "xdebug.mode", "xdebug.start_with_request" }) ini.Remove(Xdebug, setting);
        AtomicFile.WriteAllText(file, ini.ToString());
    }

    private string Folder(string version) => phpFolder?.Invoke(version) ?? paths.PhpRuntime(version);

    private string IniFile(string version) => Path.Combine(paths.PhpConfigDir(version), "php.ini");

    private IniDocument? Load(string version) =>
        File.Exists(IniFile(version)) ? IniDocument.Parse(File.ReadAllText(IniFile(version))) : null;

    private static bool LoadsXdebugSection(IniDocument? ini) =>
        ini is not null && ini.GetAll(Xdebug, "zend_extension").Any(v => Normalize(v).StartsWith(Xdebug, StringComparison.Ordinal));

    private static bool Enabled(IniDocument? ini, string name) =>
        ini is not null && (ini.GetAll("PHP", Key(name)).Any(v => Normalize(v) == name) || (name == Xdebug && LoadsXdebugSection(ini)));

    private static string Key(string name) => ZendExtensions.Contains(name) ? "zend_extension" : "extension";

    /// <summary>"gd", "php_gd.dll" and "C:/php/ext/php_gd.dll" all name gd.</summary>
    private static string Normalize(string value)
    {
        var name = Path.GetFileName(value.Replace('\\', '/')).ToLowerInvariant();
        if (name.EndsWith(".dll", StringComparison.Ordinal)) name = name[..^4];
        return name.StartsWith("php_", StringComparison.Ordinal) ? name[4..] : name;
    }
}
