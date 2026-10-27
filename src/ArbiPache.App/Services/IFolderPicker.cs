namespace ArbiPache.App.Services;

/// <summary>The system's folder and file pickers. Null when the user cancels.</summary>
public interface IFolderPicker
{
    Task<string?> PickFolderAsync(string title);
    Task<string?> PickProgramAsync(string title);

    /// <summary>One file matching <paramref name="patterns"/> (such as *.pfx), shown under <paramref name="filterName"/>.</summary>
    Task<string?> PickFileAsync(string title, string filterName, IReadOnlyList<string> patterns);
}
