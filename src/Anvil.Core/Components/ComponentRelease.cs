namespace Anvil.Core.Components;

public enum ComponentKind { Apache, Php, MariaDb, MySql, Xdebug, CaBundle }

/// <summary>One downloadable build, pinned by SHA-256.</summary>
/// <param name="Toolset">Compiler toolset of the build, e.g. "VS18" or "vs17".</param>
/// <param name="StripRoot">Top folder inside the zip to extract from, e.g. "Apache24".</param>
/// <param name="AppliesTo">For add-ons such as Xdebug: the component version they belong to.</param>
public sealed record ComponentRelease(ComponentKind Kind, string Version, Uri Url, string Sha256,
    string? Toolset = null, string? StripRoot = null, string? AppliesTo = null, bool Lts = false, string? Notes = null);
