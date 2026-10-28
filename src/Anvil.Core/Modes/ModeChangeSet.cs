using Anvil.Core.Agent;
using Anvil.Core.Settings;

namespace Anvil.Core.Modes;

/// <summary>One value a mode switch changes.</summary>
/// <param name="Where">"PHP", "Apache", "MariaDB", "MySQL" or "Windows Firewall".</param>
/// <param name="File">The file it is written to, relative to data\; empty when it is not a file.</param>
public sealed record SettingChange(string Where, string File, string Key, string From, string To);

/// <summary>What switching to another mode writes and restarts. Restarts lists only running services whose files change.</summary>
/// <param name="BackupPreviewPath">The folder the current files will be copied to.</param>
public sealed record ModeChangeSet(ServerMode From, ServerMode To, IReadOnlyList<SettingChange> Changes, IReadOnlyList<ServiceId> Restarts,
    string BackupPreviewPath);

/// <param name="KeepXdebug">Production keeps Xdebug loaded (it is always loaded in Development when installed).</param>
public sealed record ModeOptions(bool KeepXdebug);
