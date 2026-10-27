using System.Text.Json;
using ArbiPache.Core.IO;
using ArbiPache.Core.Paths;

namespace ArbiPache.Core.Settings;

/// <summary>Loads and saves data\arbipache.json. A file that cannot be read is moved aside, never overwritten.</summary>
public sealed class SettingsStore(AppPaths paths)
{
    private string FilePath => Path.Combine(paths.Data, "arbipache.json");

    public string? LastLoadWarning { get; private set; }

    public AppSettings Load()
    {
        LastLoadWarning = null;
        if (!File.Exists(FilePath)) return new AppSettings();
        try
        {
            return JsonFiles.Read<AppSettings>(FilePath) ?? new AppSettings();
        }
        catch (JsonException ex)
        {
            var aside = $"{FilePath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(FilePath, aside, overwrite: true);
            LastLoadWarning = $"Settings file could not be read ({ex.Message}). It was saved as {Path.GetFileName(aside)} and defaults are in use.";
            return new AppSettings();
        }
    }

    public void Save(AppSettings s) => JsonFiles.Write(FilePath, s);
}
