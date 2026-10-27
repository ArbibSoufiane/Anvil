namespace ArbiPache.Core.Database;

/// <summary>One server setting ArbiPache shows on the Database page. Its default is probed from the installed server.</summary>
/// <param name="Kind">int, size, bool, enum or text: how the value is edited.</param>
/// <param name="InitOnly">Fixed when the data folder is created; changing it later breaks the server.</param>
public sealed record DbSettingDef(string Key, string Kind, string Description, bool RequiresRestart, bool InitOnly, DbEngine[] Engines);
