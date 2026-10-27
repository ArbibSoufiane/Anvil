using ArbiPache.Core.IO;
using ArbiPache.Core.Paths;

namespace ArbiPache.Core.Components;

/// <summary>Reads catalog\components.json, the pins shipped with the app (works offline).</summary>
public sealed class ShippedCatalogProvider(AppPaths paths)
{
    public IReadOnlyList<ComponentRelease> All()
    {
        var file = Path.Combine(paths.Catalog, "components.json");
        if (!File.Exists(file)) return [];
        var doc = JsonFiles.Read<CatalogFile>(file);
        return doc?.Releases ?? [];
    }

    private sealed record CatalogFile(int SchemaVersion, IReadOnlyList<ComponentRelease> Releases);
}
