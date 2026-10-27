using System.Text.Json;
using System.Text.Json.Serialization;
using ArbiPache.Core.Paths;

namespace ArbiPache.Core.Components;

/// <summary>Reads catalog\components.json, the pins shipped with the app (works offline).</summary>
public sealed class ShippedCatalogProvider(AppPaths paths)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public IReadOnlyList<ComponentRelease> All()
    {
        var file = Path.Combine(paths.Catalog, "components.json");
        if (!File.Exists(file)) return [];
        var doc = JsonSerializer.Deserialize<CatalogFile>(File.ReadAllText(file), Json);
        return doc?.Releases ?? [];
    }

    private sealed record CatalogFile(int SchemaVersion, IReadOnlyList<ComponentRelease> Releases);
}
