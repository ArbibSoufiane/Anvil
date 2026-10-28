using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anvil.Core.Database;

/// <summary>The settings the Database page lists, from the embedded db-settings.json. Defaults come from <see cref="DefaultsProbe"/>.</summary>
public sealed class DbSettingsCatalog
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly IReadOnlyList<DbSettingDef> _all = Load();

    public IReadOnlyList<DbSettingDef> For(DbEngine e) => _all.Where(d => d.Engines.Contains(e)).ToList();

    private static List<DbSettingDef> Load()
    {
        using var stream = typeof(DbSettingsCatalog).Assembly.GetManifestResourceStream("Anvil.Database.db-settings.json")
            ?? throw new InvalidOperationException("db-settings.json is not embedded.");
        return JsonSerializer.Deserialize<List<DbSettingDef>>(stream, Options) ?? [];
    }
}
