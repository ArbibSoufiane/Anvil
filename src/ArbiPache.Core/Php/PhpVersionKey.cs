namespace ArbiPache.Core.Php;

public static class PhpVersionKey
{
    /// <summary>"8.5.5" becomes "php85".</summary>
    public static string Of(string version)
    {
        var parts = version.Split('.');
        return "php" + parts[0] + (parts.Length > 1 ? parts[1] : "");
    }

    /// <summary>"8.5.12" becomes "php8512"; used when two installed patch releases of one branch both need a pool.</summary>
    public static string Exact(string version) => "php" + version.Replace(".", "", StringComparison.Ordinal);
}
