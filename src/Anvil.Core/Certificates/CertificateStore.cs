using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Anvil.Core.IO;
using Anvil.Core.Paths;
using Anvil.Core.Sites;

namespace Anvil.Core.Certificates;

/// <summary>Certificates per site in data\certs\&lt;site id&gt;\: cert.pem, key.pem, optional chain.pem and meta.json.</summary>
public sealed class CertificateStore(AppPaths paths)
{
    private sealed record Meta(CertSource Source, IReadOnlyList<string> Names, DateTimeOffset NotAfter);

    public SiteCertificate? Get(string siteId)
    {
        var dir = DirFor(siteId);
        var meta = Path.Combine(dir, "meta.json");
        return ReadMeta(meta) is { } m ? ToCertificate(dir, m) : null;
    }

    /// <summary>
    /// Stores PEM text as given, replacing the site's previous certificate. Names and expiry are read from the certificate.
    /// The folder and the key are limited to this account and Administrators.
    /// </summary>
    public void Save(string siteId, string certPem, string keyPem, string? chainPem, CertSource src)
    {
        using var cert = X509Certificate2.CreateFromPem(certPem);
        var dir = DirFor(siteId);
        Directory.CreateDirectory(dir);
        PrivateAcl.RestrictFolder(dir);
        AtomicFile.WriteAllText(Path.Combine(dir, "cert.pem"), certPem);
        AtomicFile.WriteAllText(Path.Combine(dir, "key.pem"), keyPem);
        PrivateAcl.RestrictFile(Path.Combine(dir, "key.pem"));
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
            if (ReadMeta(meta) is { } m) all[Path.GetFileName(dir)] = ToCertificate(dir, m);
        }

        return all;
    }

    /// <summary>The DNS names then IP addresses in the certificate's subject alternative names, or its common name when it has none.</summary>
    internal static IReadOnlyList<string> NamesOf(X509Certificate2 cert)
    {
        var sans = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().ToList();
        var names = sans.SelectMany(e => e.EnumerateDnsNames()).Concat(sans.SelectMany(e => e.EnumerateIPAddresses()).Select(ip => ip.ToString())).ToList();
        return names.Count > 0 ? names : [cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false)];
    }

    /// <summary>
    /// Null when the folder has no certificate, or one from a source this build no longer serves (Let's Encrypt, a
    /// client's certificate): such a site is served over HTTP until it gets a local certificate.
    /// </summary>
    private static Meta? ReadMeta(string meta)
    {
        if (!File.Exists(meta)) return null;
        try
        {
            return JsonFiles.Read<Meta>(meta);
        }
        catch (JsonException)
        {
            return null;
        }
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
