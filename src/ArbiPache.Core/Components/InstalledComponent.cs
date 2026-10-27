namespace ArbiPache.Core.Components;

public sealed record InstalledComponent(ComponentKind Kind, string Version, string Path, string Sha256, DateTimeOffset InstalledAt);

/// <param name="Stage">"Downloading", "Extracting" or "Installed".</param>
public sealed record InstallProgress(string Stage, long Bytes, long? Total);
