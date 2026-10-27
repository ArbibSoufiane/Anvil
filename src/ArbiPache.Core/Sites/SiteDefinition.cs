namespace ArbiPache.Core.Sites;

/// <summary>How a site is reached: by a domain name, by one of this machine's IPs (single-site mode), or as an htdocs folder.</summary>
public enum SiteKind { Domain, Ip, Htdocs }

public enum SiteType { Php, Static, Proxy }

/// <summary>Where HTTPS comes from. LoadBalancer means plain HTTP here, TLS ends at the client's load balancer.</summary>
public enum CertSource { None, LetsEncrypt, Imported, LoadBalancer, LocalDev }

/// <summary>Who may open the site: anyone, the local network, this PC only, or listed addresses.</summary>
public enum AccessLevel { Everyone, Network, ThisPc, SpecificIps }

/// <summary>A sub-path served from its own folder, such as /demo on an IP site.</summary>
public sealed record SiteMount(string UrlPath, string Root, SiteType Type, string? PhpVersion);

/// <summary>One site as saved in data\sites\&lt;id&gt;.json.</summary>
public sealed record SiteDefinition
{
    public required string Id { get; init; }

    public required SiteKind Kind { get; init; }

    public required SiteType Type { get; init; }

    /// <summary>The domain, the IP (or "*" for all addresses), or the htdocs folder name.</summary>
    public required string Name { get; init; }

    /// <summary>The site's folder, or the target URL of a Proxy site.</summary>
    public required string Root { get; init; }

    /// <summary>Null uses the default PHP version.</summary>
    public string? PhpVersion { get; init; }

    public IReadOnlyList<string> Aliases { get; init; } = [];

    public IReadOnlyList<string> Index { get; init; } = ["index.php", "index.html"];

    public IReadOnlyList<SiteMount> Mounts { get; init; } = [];

    public CertSource Cert { get; init; } = CertSource.None;

    public IReadOnlyList<string> TrustedProxies { get; init; } = [];

    public bool AcceptOnlyFromProxies { get; init; }

    public AccessLevel Access { get; init; } = AccessLevel.Everyone;

    public IReadOnlyList<string> AllowedIps { get; init; } = [];

    public bool PasswordProtected { get; init; }

    public bool HttpRedirect { get; init; } = true;

    public bool AddToHosts { get; init; }

    public bool Enabled { get; init; } = true;

    public DateTimeOffset Created { get; init; }
}
