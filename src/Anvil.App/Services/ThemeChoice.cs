using Anvil.Core.IO;
using Avalonia;
using Avalonia.Styling;

namespace Anvil.App.Services;

/// <summary>
/// Dark, light, or whatever Windows uses: the signed-in user's choice, kept in their profile next to the editor choice.
/// Unknown or missing means dark, Anvil's original look.
/// </summary>
/// <param name="file">Where the choice is kept; null keeps it in memory only.</param>
public sealed class ThemeChoice(string? file)
{
    public const string Dark = "dark";
    public const string Light = "light";
    public const string System = "system";

    private string _memory = Dark;

    public static ThemeChoice ForUser() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Anvil", "theme.txt"));

    public string Theme
    {
        get
        {
            if (file is null) return _memory;
            try
            {
                return Normalize(File.ReadAllText(file));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Dark;
            }
        }
        set
        {
            value = Normalize(value);
            if (file is null)
            {
                _memory = value;
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            AtomicFile.WriteAllText(file, value);
        }
    }

    public static ThemeVariant Variant(string theme) => Normalize(theme) switch
    {
        Light => ThemeVariant.Light,
        System => ThemeVariant.Default,
        _ => ThemeVariant.Dark,
    };

    /// <summary>Recolours the whole app; does nothing before it exists.</summary>
    public static void Apply(string theme)
    {
        if (Application.Current is { } app) app.RequestedThemeVariant = Variant(theme);
    }

    private static string Normalize(string? theme) => theme?.Trim().ToLowerInvariant() switch
    {
        Light => Light,
        System => System,
        _ => Dark,
    };
}
