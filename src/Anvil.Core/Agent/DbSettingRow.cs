namespace Anvil.Core.Agent;

/// <summary>One row of the Database page's settings table.</summary>
/// <param name="Current">The value in my.ini; null runs the default.</param>
/// <param name="Default">What the installed server uses when nothing sets the key, read from the server itself.</param>
public sealed record DbSettingRow(string Key, string? Current, string? Default, string Description, bool Changed, bool RequiresRestart,
    bool InitOnly);
