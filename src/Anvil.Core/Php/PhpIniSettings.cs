using System.Text.RegularExpressions;
using Anvil.Core.IO;
using Anvil.Core.Paths;

namespace Anvil.Core.Php;

/// <summary>A php.ini setting the PHP page edits.</summary>
/// <param name="PhpDefault">What PHP uses when php.ini does not set it.</param>
public sealed record PhpIniSetting(string Key, string Label, string PhpDefault, bool IsSize, bool AllowsUnlimited);

/// <summary>The PHP page's php.ini values; a key php.ini does not set has no entry.</summary>
/// <param name="OpcacheAvailable">OPcache is built in, or its DLL is in ext.</param>
public sealed record PhpIniValues(IReadOnlyDictionary<string, string> Values, bool Opcache, bool OpcacheAvailable);

/// <summary>
/// Reads and writes the limits the PHP page edits, and OPcache, in data\php\&lt;v&gt;\php.ini, keeping the rest of the file as
/// the user left it.
/// </summary>
/// <param name="phpFolder">Where a version's files are: runtime\php\&lt;v&gt; unless it is a linked folder such as pvm's.</param>
public sealed partial class PhpIniSettings(AppPaths paths, Func<string, string>? phpFolder = null)
{
    private const string Opcache = "opcache";

    public static IReadOnlyList<PhpIniSetting> Editable { get; } =
    [
        new("memory_limit", "Memory limit", "128M", IsSize: true, AllowsUnlimited: true),
        new("max_execution_time", "Max execution time (s)", "30", IsSize: false, AllowsUnlimited: false),
        new("max_input_time", "Max input time (s)", "-1", IsSize: false, AllowsUnlimited: true),
        new("max_input_vars", "Max input vars", "1000", IsSize: false, AllowsUnlimited: false),
        new("post_max_size", "Post max size", "8M", IsSize: true, AllowsUnlimited: false),
        new("upload_max_filesize", "Upload max file size", "2M", IsSize: true, AllowsUnlimited: false),
        new("max_file_uploads", "Max file uploads", "20", IsSize: false, AllowsUnlimited: false),
        new("realpath_cache_size", "Realpath cache size", "4096K", IsSize: true, AllowsUnlimited: false),
        new("realpath_cache_ttl", "Realpath cache TTL (s)", "120", IsSize: false, AllowsUnlimited: false),
    ];

    [GeneratedRegex(@"^\d+[KMG]?$", RegexOptions.IgnoreCase)]
    private static partial Regex Size();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex Number();

    /// <summary>Why PHP would not read the value, or null when it would. An empty value leaves the setting to PHP.</summary>
    public static string? Problem(string key, string value)
    {
        if (Editable.FirstOrDefault(s => s.Key == key) is not { } setting) return $"{key} is not a setting the PHP page edits. Edit php.ini for it.";
        value = value.Trim();
        if (value.Length == 0 || (setting.AllowsUnlimited && value == "-1")) return null;
        if (setting.IsSize ? Size().IsMatch(value) : Number().IsMatch(value)) return null;
        var unlimited = setting.AllowsUnlimited ? ", or -1 for no limit" : "";
        return setting.IsSize
            ? $"{key} must be a size such as 256M or 1G{unlimited}."
            : $"{key} must be a whole number{unlimited}.";
    }

    public PhpIniValues Read(string version)
    {
        var ini = Load(version);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var setting in Editable)
            if (ini?.Get("PHP", setting.Key) is { } value) values[setting.Key] = StripComment(value);
        return new PhpIniValues(values, ini is not null && OpcacheOn(ini, version), OpcacheAvailable(version));
    }

    /// <summary>Sets each value; an empty one removes the line, so PHP's default applies.</summary>
    /// <exception cref="ArgumentException">A value PHP would not read; nothing is written.</exception>
    public void Write(string version, IReadOnlyDictionary<string, string> values)
    {
        foreach (var (key, value) in values)
            if (Problem(key, value) is { } problem) throw new ArgumentException(problem);
        var ini = IniDocument.Parse(File.ReadAllText(IniFile(version)));
        foreach (var (key, value) in values)
        {
            if (value.Trim() is { Length: > 0 } v) ini.Set("PHP", key, v.ToUpperInvariant());
            else ini.Remove("PHP", key);
        }
        AtomicFile.WriteAllText(IniFile(version), ini.ToString());
    }

    /// <summary>On loads OPcache (built in from PHP 8.5) and sets opcache.enable=1; off sets opcache.enable=0.</summary>
    /// <exception cref="ArgumentException">OPcache is not in this PHP's ext folder.</exception>
    public void SetOpcache(string version, bool enabled)
    {
        var ini = IniDocument.Parse(File.ReadAllText(IniFile(version)));
        if (enabled && !PhpSupport.HasBuiltInOpcache(version))
        {
            if (!OpcacheAvailable(version)) throw new ArgumentException($"PHP {version} has no php_opcache.dll in its ext folder.");
            if (!PhpExtensionManager.Loads(ini, Opcache)) ini.AddValue("PHP", "zend_extension", Opcache);
        }
        // A value in [PHP] would win over the one in [opcache] if it came later in the file.
        ini.Remove("PHP", "opcache.enable");
        ini.Set(Opcache, "opcache.enable", enabled ? "1" : "0");
        AtomicFile.WriteAllText(IniFile(version), ini.ToString());
    }

    private bool OpcacheAvailable(string version) =>
        PhpSupport.HasBuiltInOpcache(version) || File.Exists(Path.Combine(Folder(version), "ext", "php_opcache.dll"));

    // opcache.enable is 1 unless php.ini says otherwise.
    private static bool OpcacheOn(IniDocument ini, string version) =>
        (PhpSupport.HasBuiltInOpcache(version) || PhpExtensionManager.Loads(ini, Opcache))
        && ((ini.Get(Opcache, "opcache.enable") ?? ini.Get("PHP", "opcache.enable")) is not { } value || !IsOff(StripComment(value)));

    private static bool IsOff(string value) =>
        value.Length == 0 || value is "0" || value.Equals("off", StringComparison.OrdinalIgnoreCase)
        || value.Equals("false", StringComparison.OrdinalIgnoreCase) || value.Equals("no", StringComparison.OrdinalIgnoreCase);

    /// <summary>"256M ; the most a request may post" is 256M.</summary>
    private static string StripComment(string value) => (value.IndexOf(';') is var i and >= 0 ? value[..i] : value).Trim().Trim('"');

    private string Folder(string version) => phpFolder?.Invoke(version) ?? paths.PhpRuntime(version);

    private string IniFile(string version) => Path.Combine(paths.PhpConfigDir(version), "php.ini");

    private IniDocument? Load(string version) =>
        File.Exists(IniFile(version)) ? IniDocument.Parse(File.ReadAllText(IniFile(version))) : null;
}
