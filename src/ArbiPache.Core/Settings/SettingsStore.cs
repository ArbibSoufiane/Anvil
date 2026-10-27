using System.Text.Json;
using System.Text.Json.Serialization;
using ArbiPache.Core.IO;
using ArbiPache.Core.Paths;

namespace ArbiPache.Core.Settings;

/// <summary>Loads and saves data\arbipache.json. A file that cannot be read is moved aside, never overwritten.</summary>
public sealed class SettingsStore(AppPaths paths)
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private string FilePath => Path.Combine(paths.Data, "arbipache.json");

    public string? LastLoadWarning { get; private set; }

    public AppSettings Load()
    {
        LastLoadWarning = null;
        if (!File.Exists(FilePath)) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch (JsonException ex)
        {
            var aside = $"{FilePath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(FilePath, aside, overwrite: true);
            LastLoadWarning = $"Settings file could not be read ({ex.Message}). It was saved as {Path.GetFileName(aside)} and defaults are in use.";
            return new AppSettings();
        }
    }

    public void Save(AppSettings s) => AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(s, Json));
}
