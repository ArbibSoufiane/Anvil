using ArbiPache.Core.IO;

namespace ArbiPache.App.Services;

/// <summary>
/// The program that opens config files: "auto", or a path the signed-in user chose. It stays in their profile, not in
/// data\: the service account may write data\, so a program named there could be planted by a script a site runs.
/// </summary>
/// <param name="file">Where the choice is kept; null keeps it in memory only.</param>
public sealed class EditorChoice(string? file)
{
    private string _memory = "auto";

    public static EditorChoice ForUser() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArbiPache", "editor.txt"));

    public string Editor
    {
        get
        {
            if (file is null) return _memory;
            try
            {
                var chosen = File.ReadAllText(file).Trim();
                return chosen.Length > 0 ? chosen : "auto";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return "auto";
            }
        }
        set
        {
            if (file is null)
            {
                _memory = value;
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            AtomicFile.WriteAllText(file, value);
        }
    }
}
