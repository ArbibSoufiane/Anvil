using ArbiPache.Core.Modes;
using ArbiPache.Core.Php;
using ArbiPache.Core.Settings;

namespace ArbiPache.Core.Apache;

/// <summary>Everything the generated Apache configuration depends on. Later features add their own properties.</summary>
public sealed record RenderInput
{
    public required AppSettings Settings { get; init; }
    public required StockConfState Stock { get; init; }
    public ModeProfile Profile { get; init; } = BuiltInProfiles.Development;
    public string? ApacheVersion { get; init; }

    /// <summary>The PHP pools that will run, from <see cref="PhpPoolPlanner"/>. Empty means no PHP handler anywhere.</summary>
    public IReadOnlyList<PhpPool> Pools { get; init; } = [];
}
