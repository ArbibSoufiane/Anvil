namespace ArbiPache.Core.Components;

/// <param name="Toolset">The release's compiler toolset, e.g. "vs17"; null for entries recorded before it was kept.</param>
/// <param name="AppliesTo">For an Xdebug build, the PHP minor it loads into, e.g. "8.5".</param>
public sealed record InstalledComponent(ComponentKind Kind, string Version, string Path, string Sha256, DateTimeOffset InstalledAt,
    string? Toolset = null, string? AppliesTo = null);

/// <param name="Stage">"Downloading", "Extracting" or "Installed".</param>
public sealed record InstallProgress(string Stage, long Bytes, long? Total);
