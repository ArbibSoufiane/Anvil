using Serilog;

namespace ArbiPache.Core.Components;

/// <summary>Online releases merged over the shipped pins, newest first. An unreachable source falls back to the pins.</summary>
public sealed class ComponentCatalog(IEnumerable<ICatalogProvider> online, ShippedCatalogProvider shipped)
{
    public async Task<IReadOnlyList<ComponentRelease>> GetAsync(ComponentKind kind, CancellationToken ct)
    {
        var merged = new List<ComponentRelease>();
        foreach (var provider in online.Where(p => p.Kind == kind))
        {
            try
            {
                merged.AddRange(await provider.GetAsync(ct));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.Warning(ex, "Online catalog for {Kind} unavailable; using shipped pins", kind);
            }
        }
        merged.AddRange(shipped.All().Where(r => r.Kind == kind));

        // First occurrence wins, so an online entry replaces the shipped pin of the same build.
        return merged
            .Where(r => r.Kind == kind)
            .DistinctBy(r => (r.Version.ToLowerInvariant(), r.Toolset?.ToLowerInvariant(), r.AppliesTo?.ToLowerInvariant()))
            .OrderByDescending(r => r.Version, Comparer<string>.Create(SemVer.Compare))
            .ToList();
    }
}
