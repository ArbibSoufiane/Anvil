namespace Anvil.Core.Php;

public static class PortAllocator
{
    /// <summary>
    /// The key's existing port while it is still free, else the first free port in the range that no other key owns.
    /// The caller's <paramref name="isFree"/> must count ports Anvil itself already listens on as free.
    /// </summary>
    public static int Allocate(string key, IReadOnlyDictionary<string, int> existing, Func<int, bool> isFree, int from = 9100, int to = 9999)
    {
        var others = existing.Where(e => !string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)).Select(e => e.Value).ToHashSet();
        if (existing.TryGetValue(key, out var current) && !others.Contains(current) && isFree(current)) return current;
        for (var port = from; port <= to; port++)
            if (!others.Contains(port) && isFree(port)) return port;
        throw new InvalidOperationException($"No free port for a PHP pool in {from}-{to}.");
    }
}
