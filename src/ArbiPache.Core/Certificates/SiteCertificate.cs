using ArbiPache.Core.Sites;

namespace ArbiPache.Core.Certificates;

/// <summary>A site's certificate on disk. ChainFile is null when the issuer sent no intermediate certificates.</summary>
public sealed record SiteCertificate(
    CertSource Source, IReadOnlyList<string> Names, DateTimeOffset NotAfter, string CertFile, string KeyFile, string? ChainFile);
