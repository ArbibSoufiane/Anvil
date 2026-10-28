using Anvil.Core.Certificates;
using Anvil.Core.Php;
using Anvil.Core.Settings;
using Anvil.Core.Sites;

namespace Anvil.Core.Apache;

/// <summary>Everything the generated Apache configuration depends on. Later features add their own properties.</summary>
public sealed record RenderInput
{
    public required AppSettings Settings { get; init; }
    public required StockConfState Stock { get; init; }
    public string? ApacheVersion { get; init; }

    /// <summary>The PHP pools that will run, from <see cref="PhpPoolPlanner"/>. Empty means no PHP handler anywhere.</summary>
    public IReadOnlyList<PhpPool> Pools { get; init; } = [];

    /// <summary>Saved sites. Disabled sites render nothing; htdocs sites only shape <see cref="Htdocs"/> entries.</summary>
    public IReadOnlyList<SiteDefinition> Sites { get; init; } = [];

    /// <summary>What localhost/&lt;folder&gt; serves, from <see cref="SiteSet.DiscoverHtdocs"/>.</summary>
    public IReadOnlyList<HtdocsEntry> Htdocs { get; init; } = [];

    /// <summary>Certificates by site id. An HTTPS site without one is served over HTTP until it has one.</summary>
    public IReadOnlyDictionary<string, SiteCertificate> Certificates { get; init; } = new Dictionary<string, SiteCertificate>();
}
