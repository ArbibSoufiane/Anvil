using Anvil.Core.Components;
using Anvil.Core.IO;
using Anvil.Core.Paths;

namespace Anvil.Core.Php;

/// <param name="Available">ext\php_&lt;name&gt;.dll exists in the version's runtime, or for Xdebug, a build for its branch.</param>
public sealed record PhpExtension(string Name, bool Enabled, bool Available, bool IsZend);

/// <summary>
/// Turns extensions on and off in data\php\&lt;v&gt;\php.ini, keeping the rest of the file as the user left it. Xdebug installed
/// from the catalog lives in runtime\xdebug and loads from an [xdebug] section; <paramref name="xdebugBuilds"/> are those builds.
/// A PHP folder of the user's, such as pvm's, may bring its own Xdebug in ext, and php.ini may load an extension by its full path.
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
        var ext = ExtDir(version);
        if (!Directory.Exists(ext)) return [];
        var ini = Load(version);
        var names = Directory.GetFiles(ext, "php_*.dll").Select(f => Normalize(Path.GetFileName(f)))
            .Concat(ini is null ? [] : ElsewhereOnDisk(ini).Keys);
        // A catalog build of Xdebug shows even without a DLL in ext, and so does one php.ini still loads.
        var xdebugElsewhere = XdebugDll(xdebugBuilds ?? [], version) is not null || LoadsXdebugSection(ini);
        return (xdebugElsewhere ? names.Append(Xdebug) : names)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .Select(name => new PhpExtension(name, Enabled(ini, name), Available: true, ZendExtensions.Contains(name)))
            .ToList();
    }

    public void Set(string version, string name, bool enabled)
    {
        name = name.ToLowerInvariant();
        var file = IniFile(version);
        var ini = IniDocument.Parse(File.ReadAllText(file));
        var isXdebug = name == Xdebug;
        if (isXdebug && enabled && XdebugDll(xdebugBuilds ?? [], version) is { } dll)
        {
            ini.Remove(Xdebug, "zend_extension");
            ini.AddValue(Xdebug, "zend_extension", $"\"{AppPaths.ForApache(dll)}\"");
            ini.Set(Xdebug, "xdebug.mode", "debug,develop");
            ini.Set(Xdebug, "xdebug.start_with_request", "trigger");
            AtomicFile.WriteAllText(file, ini.ToString());
            return;
        }

        var key = Key(name);
        if (enabled && !Enabled(ini, name))
        {
            var value = InExt(version, name) ?? (ElsewhereOnDisk(ini).TryGetValue(name, out var path) ? Quote(path) : null)
                ?? throw new ArgumentException(isXdebug
                    ? $"No Xdebug build is installed for PHP {PhpSupport.Minor(version)}. Install it from the PHP page first."
                    : $"PHP {version} has no php_{name}.dll in its ext folder.");
            ini.AddValue("PHP", key, value);
            // Xdebug from the PHP's own folder starts with the same settings as Anvil's build, unless php.ini has its own.
            if (isXdebug && !ini.GetAll(Xdebug, "xdebug.mode").Concat(ini.GetAll("PHP", "xdebug.mode")).Any())
            {
                ini.Set(Xdebug, "xdebug.mode", "debug,develop");
                ini.Set(Xdebug, "xdebug.start_with_request", "trigger");
            }
        }
        if (!enabled)
        {
            foreach (var value in ini.GetAll("PHP", key).Where(v => Normalize(v) == name).ToList()) ini.RemoveValue("PHP", key, value);
            if (isXdebug)
                foreach (var setting in new[] { "zend_extension", "xdebug.mode", "xdebug.start_with_request" }) ini.Remove(Xdebug, setting);
        }
        AtomicFile.WriteAllText(file, ini.ToString());
    }

    /// <summary>
    /// The name php.ini loads the extension by from ext: "gd" for php_gd.dll, or the file name of a build named after its
    /// version, such as php_xdebug-3.4.5-8.3-ts-vs16-x86_64.dll.
    /// </summary>
    private string? InExt(string version, string name)
    {
        var ext = ExtDir(version);
        if (File.Exists(Path.Combine(ext, $"php_{name}.dll"))) return name;
        if (name != Xdebug || !Directory.Exists(ext)) return null;
        return Directory.GetFiles(ext, "php_xdebug*.dll").Order(StringComparer.Ordinal).Select(Path.GetFileName).FirstOrDefault();
    }

    private string Folder(string version) => phpFolder?.Invoke(version) ?? paths.PhpRuntime(version);

    private string ExtDir(string version) => Path.Combine(Folder(version), "ext");

    private string IniFile(string version) => Path.Combine(paths.PhpConfigDir(version), "php.ini");

    private IniDocument? Load(string version) =>
        File.Exists(IniFile(version)) ? IniDocument.Parse(File.ReadAllText(IniFile(version))) : null;

    /// <summary>Extensions php.ini names by a full path outside ext, switched on or commented out, whose DLL exists.</summary>
    private static Dictionary<string, string> ElsewhereOnDisk(IniDocument ini)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in new[] { "extension", "zend_extension" })
            foreach (var value in ini.GetAll("PHP", key).Concat(ini.GetAllCommented("PHP", key)))
                if (Path.IsPathFullyQualified(value) && File.Exists(value)) found.TryAdd(Normalize(value), value);
        return found;
    }

    /// <summary>php.ini loads the extension, by any spelling, as an extension or a zend_extension.</summary>
    internal static bool Loads(IniDocument ini, string name) =>
        Enabled(ini, name) || new[] { "extension", "zend_extension" }.Any(k => ini.GetAll("PHP", k).Any(v => Normalize(v) == name));

    /// <summary>The extension a php.ini value names.</summary>
    internal static string NameOf(string value) => Normalize(value);

    private static bool LoadsXdebugSection(IniDocument? ini) =>
        ini is not null && ini.GetAll(Xdebug, "zend_extension").Any(v => Normalize(v) == Xdebug);

    private static bool Enabled(IniDocument? ini, string name) =>
        ini is not null && (ini.GetAll("PHP", Key(name)).Any(v => Normalize(v) == name) || (name == Xdebug && LoadsXdebugSection(ini)));

    private static string Key(string name) => ZendExtensions.Contains(name) ? "zend_extension" : "extension";

    private static string Quote(string path) => $"\"{AppPaths.ForApache(path)}\"";

    /// <summary>
    /// "gd", "php_gd.dll" and "C:/php/ext/php_gd.dll" all name gd; an Xdebug build named after its version, such as
    /// php_xdebug-3.4.5-8.3-vs16-x86_64.dll, names xdebug.
    /// </summary>
    private static string Normalize(string value)
    {
        var name = Path.GetFileName(value.Replace('\\', '/')).ToLowerInvariant();
        if (name.EndsWith(".dll", StringComparison.Ordinal)) name = name[..^4];
        if (name.StartsWith("php_", StringComparison.Ordinal)) name = name[4..];
        return name.StartsWith(Xdebug + "-", StringComparison.Ordinal) ? Xdebug : name;
    }
}
