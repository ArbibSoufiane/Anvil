using ArbiPache.Core.IO;
using ArbiPache.Core.Paths;

namespace ArbiPache.Core.Php;

/// <param name="Available">ext\php_&lt;name&gt;.dll exists in the version's runtime.</param>
/// <param name="ManagedByMode">The Dev/Production mode switches it, not the user (Xdebug).</param>
public sealed record PhpExtension(string Name, bool Enabled, bool Available, bool IsZend, bool ManagedByMode);

/// <summary>Turns extensions on and off in data\php\&lt;v&gt;\php.ini, keeping the rest of the file as the user left it.</summary>
public sealed class PhpExtensionManager(AppPaths paths)
{
    private const string Xdebug = "xdebug";
    private static readonly HashSet<string> ZendExtensions = new(StringComparer.OrdinalIgnoreCase) { "opcache", Xdebug };

    public IReadOnlyList<PhpExtension> List(string version)
    {
        var ext = Path.Combine(paths.PhpRuntime(version), "ext");
        if (!Directory.Exists(ext)) return [];
        var ini = Load(version);
        return Directory.GetFiles(ext, "php_*.dll")
            .Select(f => Path.GetFileNameWithoutExtension(f)["php_".Length..].ToLowerInvariant())
            .Order(StringComparer.Ordinal)
            .Select(name => new PhpExtension(name, Enabled(ini, name), Available: true, ZendExtensions.Contains(name), ManagedByMode: name == Xdebug))
            .ToList();
    }

    public void Set(string version, string name, bool enabled)
    {
        if (string.Equals(name, Xdebug, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Xdebug follows the Dev/Production mode: it is on in Development and off in Production.");
        var dll = $"php_{name}.dll";
        if (enabled && !File.Exists(Path.Combine(paths.PhpRuntime(version), "ext", dll)))
            throw new ArgumentException($"PHP {version} has no {dll} in its ext folder.", nameof(name));

        var file = IniFile(version);
        var ini = IniDocument.Parse(File.ReadAllText(file));
        var key = Key(name);
        var current = ini.GetAll("PHP", key).Where(v => Normalize(v) == name.ToLowerInvariant()).ToList();
        if (enabled && current.Count == 0) ini.AddValue("PHP", key, name);
        if (!enabled) foreach (var value in current) ini.RemoveValue("PHP", key, value);
        AtomicFile.WriteAllText(file, ini.ToString());
    }

    private string IniFile(string version) => Path.Combine(paths.PhpConfigDir(version), "php.ini");

    private IniDocument? Load(string version) =>
        File.Exists(IniFile(version)) ? IniDocument.Parse(File.ReadAllText(IniFile(version))) : null;

    private static bool Enabled(IniDocument? ini, string name) =>
        ini is not null && ini.GetAll("PHP", Key(name)).Any(v => Normalize(v) == name);

    private static string Key(string name) => ZendExtensions.Contains(name) ? "zend_extension" : "extension";

    /// <summary>"gd", "php_gd.dll" and "C:/php/ext/php_gd.dll" all name gd.</summary>
    private static string Normalize(string value)
    {
        var name = Path.GetFileName(value.Replace('\\', '/')).ToLowerInvariant();
        if (name.EndsWith(".dll", StringComparison.Ordinal)) name = name[..^4];
        return name.StartsWith("php_", StringComparison.Ordinal) ? name[4..] : name;
    }
}
