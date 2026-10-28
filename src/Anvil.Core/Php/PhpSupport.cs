namespace Anvil.Core.Php;

/// <summary>Facts about PHP branches that the feed does not carry.</summary>
public static class PhpSupport
{
    // End of security support per branch, from php.net/supported-versions.
    private static readonly Dictionary<string, DateOnly> EndOfLife = new()
    {
        ["7.4"] = new(2022, 11, 28),
        ["8.0"] = new(2023, 11, 26),
        ["8.1"] = new(2025, 12, 31),
        ["8.2"] = new(2026, 12, 31),
        ["8.3"] = new(2027, 12, 31),
        ["8.4"] = new(2028, 12, 31),
        ["8.5"] = new(2029, 12, 31),
    };

    public static bool IsEndOfLife(string version, DateOnly today)
    {
        var (major, minor) = Branch(version);
        if (EndOfLife.TryGetValue($"{major}.{minor}", out var end)) return today > end;
        // Branches older than the table are long dead; newer ones are not dead yet.
        return (major, minor).CompareTo((7, 4)) < 0;
    }

    /// <summary>OPcache is compiled into PHP from 8.5; older versions load it as a zend_extension.</summary>
    public static bool HasBuiltInOpcache(string version) => Branch(version).CompareTo((8, 5)) >= 0;

    /// <summary>"8.5.12" is "8.5": what Xdebug builds and the pool summary go by.</summary>
    public static string Minor(string version)
    {
        var (major, minor) = Branch(version);
        return $"{major}.{minor}";
    }

    private static (int Major, int Minor) Branch(string version)
    {
        var parts = version.Split('.');
        return (int.Parse(parts[0]), parts.Length > 1 ? int.Parse(parts[1]) : 0);
    }
}
