namespace Anvil.Core.Sites;

/// <summary>How a site is reached: by a domain name, or as an htdocs folder.</summary>
public enum SiteKind { Domain, Htdocs }

public enum SiteType { Php, Static, Proxy }

/// <summary>Where HTTPS comes from: nowhere (plain HTTP), or a certificate from Anvil's local CA.</summary>
public enum CertSource { None, LocalDev }

/// <summary>Who may open the site: this PC only, or the local network as well.</summary>
public enum AccessLevel { ThisPc, Network }

/// <summary>One site as saved in data\sites\&lt;id&gt;.json.</summary>
public sealed record SiteDefinition
{
    public required string Id { get; init; }

    public required SiteKind Kind { get; init; }

    public required SiteType Type { get; init; }

    /// <summary>The domain, or the htdocs folder name.</summary>
    public required string Name { get; init; }

    /// <summary>The site's folder, or the target URL of a Proxy site.</summary>
    public required string Root { get; init; }

    /// <summary>Null uses the default PHP version.</summary>
    public string? PhpVersion { get; init; }

    public IReadOnlyList<string> Aliases { get; init; } = [];

    public IReadOnlyList<string> Index { get; init; } = ["index.php", "index.html"];

    public CertSource Cert { get; init; } = CertSource.None;

    public AccessLevel Access { get; init; } = AccessLevel.Network;

    public bool HttpRedirect { get; init; } = true;

    public bool AddToHosts { get; init; }

    public bool Enabled { get; init; } = true;

    public DateTimeOffset Created { get; init; }
}
