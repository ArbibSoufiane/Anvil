namespace ArbiPache.Core.Database;

/// <summary>MariaDB and MySQL treat "max-connections" and "max_connections" as the same option.</summary>
internal sealed class OptionNames : IEqualityComparer<string>
{
    public static readonly OptionNames Comparer = new();

    public bool Equals(string? x, string? y) =>
        string.Equals(x?.Replace('-', '_'), y?.Replace('-', '_'), StringComparison.OrdinalIgnoreCase);

    public int GetHashCode(string obj) => StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Replace('-', '_'));
}
