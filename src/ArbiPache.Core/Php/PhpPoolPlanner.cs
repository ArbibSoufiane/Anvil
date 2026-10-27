using ArbiPache.Core.Components;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Settings;

namespace ArbiPache.Core.Php;

/// <summary>Decides which PHP pools run, on which ports, with how many workers.</summary>
public sealed class PhpPoolPlanner(AppPaths paths)
{
    public const int DefaultMaxRequests = 1000;

    /// <param name="requiredVersions">DefaultPhp plus the versions enabled sites use. Versions that are not installed get no pool.</param>
    public IReadOnlyList<PhpPool> Plan(AppSettings s, IReadOnlyCollection<string> requiredVersions, IReadOnlyList<InstalledComponent> php, Func<int, bool> isFree)
    {
        var versions = requiredVersions
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(v => php.FirstOrDefault(i => i.Kind == ComponentKind.Php && string.Equals(i.Version, v, StringComparison.OrdinalIgnoreCase)))
            .OfType<InstalledComponent>()
            .OrderByDescending(i => i.Version, Comparer<string>.Create(SemVer.Compare))
            .ToList();
        // Two patch releases of one branch cannot share "php85".
        var crowded = versions.GroupBy(i => PhpVersionKey.Of(i.Version)).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();

        var ports = new Dictionary<string, int>(s.PhpPoolPorts, StringComparer.OrdinalIgnoreCase);
        var pools = new List<PhpPool>();
        foreach (var installed in versions)
        {
            var version = installed.Version;
            var key = crowded.Contains(PhpVersionKey.Of(version)) ? PhpVersionKey.Exact(version) : PhpVersionKey.Of(version);
            var port = PortAllocator.Allocate(key, ports, isFree);
            ports[key] = port;
            var tuning = TuningFor(s, version);
            var iniDir = paths.PhpConfigDir(version);
            pools.Add(new PhpPool(version, key, port, tuning.Workers, tuning.MaxRequests,
                Path.Combine(installed.Path, "php-cgi.exe"), iniDir, Path.Combine(iniDir, "conf.d")));
        }
        return pools;
    }

    /// <summary>The saved pool settings for <paramref name="version"/>, or half the cores and <see cref="DefaultMaxRequests"/>.</summary>
    public static PhpPoolSettings TuningFor(AppSettings s, string version) =>
        s.PhpPools.TryGetValue(version, out var t) ? t : new PhpPoolSettings(Suggest.PhpWorkers(Environment.ProcessorCount), DefaultMaxRequests);

    /// <summary>What to persist as <see cref="AppSettings.PhpPoolPorts"/>.</summary>
    public static IReadOnlyDictionary<string, int> Allocations(IEnumerable<PhpPool> pools) => pools.ToDictionary(p => p.Key, p => p.Port);
}
