namespace ArbiPache.App.Services;

/// <summary>The system's folder and file pickers. Null when the user cancels.</summary>
public interface IFolderPicker
{
    Task<string?> PickFolderAsync(string title);
    Task<string?> PickProgramAsync(string title);
}
