namespace Anvil.Core.Agent;

/// <summary>One row of the Database page's settings table.</summary>
/// <param name="Current">The configured value (my.ini, or the mode file when <paramref name="SetByMode"/>); null runs the default.</param>
/// <param name="Default">What the installed server uses when nothing sets the key, read from the server itself.</param>
/// <param name="SetByMode">Development or Production sets this key; it changes on the Dev / Production page.</param>
public sealed record DbSettingRow(string Key, string? Current, string? Default, string Description, bool Changed, bool SetByMode,
    bool RequiresRestart, bool InitOnly);
