using ArbiPache.Core.Modes;
using ArbiPache.Core.Settings;

namespace ArbiPache.Core.Apache;

/// <summary>Everything the generated Apache configuration depends on. Later features add their own properties.</summary>
public sealed record RenderInput
{
    public required AppSettings Settings { get; init; }
    public required StockConfState Stock { get; init; }
    public ModeProfile Profile { get; init; } = BuiltInProfiles.Development;
    public string? ApacheVersion { get; init; }
}
