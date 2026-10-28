using System.Text.Json;
using Anvil.Core.IO;
using Anvil.Core.Paths;
using Anvil.Core.Sites;

namespace Anvil.Core.Settings;

/// <summary>Loads and saves data\anvil.json. A file that cannot be read is moved aside, never overwritten.</summary>
public sealed class SettingsStore(AppPaths paths)
{
    private string FilePath => Path.Combine(paths.Data, "anvil.json");

    public string? LastLoadWarning { get; private set; }

    public AppSettings Load()
    {
        LastLoadWarning = null;
        if (!File.Exists(FilePath)) return new AppSettings();
        try
        {
            return Normalized(JsonFiles.Read<AppSettings>(FilePath));
        }
        catch (JsonException ex)
        {
            var aside = $"{FilePath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(FilePath, aside, overwrite: true);
            LastLoadWarning = $"Settings file could not be read ({ex.Message}). It was saved as {Path.GetFileName(aside)} and defaults are in use.";
            return new AppSettings();
        }
    }

    /// <summary>
    /// The settings, the defaults when there is no file, or null when the file cannot be read. Never moves or writes
    /// anything: the Elevator reads settings as administrator and must leave the user's file alone.
    /// </summary>
    public AppSettings? TryRead()
    {
        if (!File.Exists(FilePath)) return new AppSettings();
        try
        {
            return Normalized(JsonFiles.Read<AppSettings>(FilePath));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(AppSettings s) => JsonFiles.Write(FilePath, s);

    // A hand-edited domain reads the way the app saves one: name lookups arrive in lower case.
    private static AppSettings Normalized(AppSettings? s) => s is null
        ? new AppSettings()
        : s with { NetworkDomain = NetworkNames.NormalizeDomain(s.NetworkDomain) };
}
