using System.Security.Cryptography.X509Certificates;
using ArbiPache.Core.IO;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Sites;

namespace ArbiPache.Core.Certificates;

/// <summary>Certificates per site in data\certs\&lt;site id&gt;\: cert.pem, key.pem, optional chain.pem and meta.json.</summary>
public sealed class CertificateStore(AppPaths paths)
{
    private sealed record Meta(CertSource Source, IReadOnlyList<string> Names, DateTimeOffset NotAfter);

    public SiteCertificate? Get(string siteId)
    {
        var dir = DirFor(siteId);
        var meta = Path.Combine(dir, "meta.json");
        return File.Exists(meta) && JsonFiles.Read<Meta>(meta) is { } m ? ToCertificate(dir, m) : null;
    }

    /// <summary>Stores PEM text as given, replacing the site's previous certificate. Names and expiry are read from the certificate.</summary>
    public void Save(string siteId, string certPem, string keyPem, string? chainPem, CertSource src)
    {
        using var cert = X509Certificate2.CreateFromPem(certPem);
        var dir = DirFor(siteId);
        Directory.CreateDirectory(dir);
        AtomicFile.WriteAllText(Path.Combine(dir, "cert.pem"), certPem);
        AtomicFile.WriteAllText(Path.Combine(dir, "key.pem"), keyPem);
        var chain = Path.Combine(dir, "chain.pem");
        if (chainPem is null) File.Delete(chain);
        else AtomicFile.WriteAllText(chain, chainPem);
        JsonFiles.Write(Path.Combine(dir, "meta.json"), new Meta(src, NamesOf(cert), new DateTimeOffset(cert.NotAfter.ToUniversalTime(), TimeSpan.Zero)));
    }

    public void Delete(string siteId)
    {
        var dir = DirFor(siteId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    public IReadOnlyDictionary<string, SiteCertificate> All()
    {
        var all = new SortedDictionary<string, SiteCertificate>(StringComparer.Ordinal);
        if (!Directory.Exists(paths.Certs)) return all;
        foreach (var dir in Directory.GetDirectories(paths.Certs))
        {
            var meta = Path.Combine(dir, "meta.json");
            if (File.Exists(meta) && JsonFiles.Read<Meta>(meta) is { } m) all[Path.GetFileName(dir)] = ToCertificate(dir, m);
        }

        return all;
    }

    /// <summary>The DNS names in the certificate's subject alternative names, or its common name when it has none.</summary>
    internal static IReadOnlyList<string> NamesOf(X509Certificate2 cert)
    {
        var names = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().SelectMany(e => e.EnumerateDnsNames()).ToList();
        return names.Count > 0 ? names : [cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false)];
    }

    private static SiteCertificate ToCertificate(string dir, Meta m)
    {
        var chain = Path.Combine(dir, "chain.pem");
        return new SiteCertificate(m.Source, m.Names, m.NotAfter, Path.Combine(dir, "cert.pem"), Path.Combine(dir, "key.pem"),
            File.Exists(chain) ? chain : null);
    }

    private string DirFor(string siteId)
    {
        SiteStore.EnsureSafeId(siteId);
        return Path.Combine(paths.Certs, siteId);
    }
}
