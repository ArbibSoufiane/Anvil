using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ArbiPache.Core.Sites;

namespace ArbiPache.Core.Certificates;

/// <summary>A certificate a client sent: a .pfx file with its password, or a certificate file, its private key file and an optional chain file.</summary>
public sealed record CertImportRequest(string? PfxPath, string? PfxPassword, string? CertPath, string? KeyPath, string? ChainPath);

/// <summary>Checks a client's certificate against the site and stores it as PEM: certificate, PKCS#8 key and the chain in issuer order.</summary>
public sealed class CertImporter(CertificateStore store, TimeProvider time)
{
    private const int ErrorInvalidPassword = unchecked((int)0x80070056);

    public SiteCertificate Import(string siteId, IReadOnlyList<string> siteNames, CertImportRequest r)
    {
        var (certs, key) = r.PfxPath is not null ? FromPfx(r.PfxPath, r.PfxPassword) : FromPem(r);
        try
        {
            var publicKey = key.ExportSubjectPublicKeyInfo();
            var leaf = certs.FirstOrDefault(c => c.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(publicKey))
                ?? throw new CertificateImportException("key-mismatch", "The private key does not belong to this certificate. Ask the client for the matching key.");
            var notAfter = new DateTimeOffset(leaf.NotAfter.ToUniversalTime(), TimeSpan.Zero);
            if (time.GetUtcNow() >= notAfter)
                throw new CertificateImportException("expired", $"The certificate expired on {notAfter:yyyy-MM-dd}. Ask the client for a renewed one.");
            var names = CertificateStore.NamesOf(leaf);
            var missing = siteNames.Where(n => !NameMatch.Covers(names, n)).ToList();
            if (missing.Count > 0)
                throw new CertificateImportException("names-not-covered",
                    $"The certificate does not cover {string.Join(", ", missing)}. It is for {string.Join(", ", names)}.");
            var chain = IssuerOrder(leaf, certs);
            store.Save(siteId, leaf.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem(),
                chain.Count == 0 ? null : string.Join("\n", chain.Select(c => c.ExportCertificatePem())), CertSource.Imported);
            return store.Get(siteId)!;
        }
        finally
        {
            key.Dispose();
            foreach (var cert in certs) cert.Dispose();
        }
    }

    private static (List<X509Certificate2> Certs, AsymmetricAlgorithm Key) FromPfx(string path, string? password)
    {
        X509Certificate2Collection all;
        try
        {
            all = X509CertificateLoader.LoadPkcs12Collection(Read(path), password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException e) when (e.HResult == ErrorInvalidPassword)
        {
            throw new CertificateImportException("bad-password", "The password does not open this .pfx file.");
        }
        catch (CryptographicException)
        {
            throw Unreadable(path, "is not a .pfx certificate file");
        }

        var certs = all.ToList();
        var holder = certs.FirstOrDefault(c => c.HasPrivateKey);
        AsymmetricAlgorithm? key = holder?.GetRSAPrivateKey() ?? (AsymmetricAlgorithm?)holder?.GetECDsaPrivateKey();
        if (key is not null) return (certs, key);
        foreach (var cert in certs) cert.Dispose();
        throw Unreadable(path, "holds no RSA or ECDSA private key");
    }

    private static (List<X509Certificate2> Certs, AsymmetricAlgorithm Key) FromPem(CertImportRequest r)
    {
        if (r.CertPath is null || r.KeyPath is null)
            throw new CertificateImportException("unreadable", "Choose a .pfx file, or the certificate file together with its private key file.");
        var certs = Certificates(r.CertPath);
        if (r.ChainPath is not null) certs.AddRange(Certificates(r.ChainPath));
        return (certs, PrivateKey(r.KeyPath));
    }

    /// <summary>Every certificate in a PEM file, or the single certificate in a DER (.cer) file.</summary>
    private static List<X509Certificate2> Certificates(string path)
    {
        var bytes = Read(path);
        try
        {
            var text = Encoding.UTF8.GetString(bytes);
            if (!text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)) return [X509CertificateLoader.LoadCertificate(bytes)];
            var collection = new X509Certificate2Collection();
            collection.ImportFromPem(text);
            return [.. collection];
        }
        catch (CryptographicException)
        {
            throw Unreadable(path, "is not a certificate");
        }
    }

    private static AsymmetricAlgorithm PrivateKey(string path)
    {
        var text = Encoding.UTF8.GetString(Read(path));
        if (text.Contains("ENCRYPTED PRIVATE KEY", StringComparison.Ordinal))
            throw new CertificateImportException("unreadable",
                $"{Path.GetFileName(path)} is protected with a password. Ask for the key without one, or for a .pfx file.");
        foreach (var create in new Func<AsymmetricAlgorithm>[] { RSA.Create, ECDsa.Create })
        {
            var key = create();
            try
            {
                key.ImportFromPem(text);
                return key;
            }
            catch (Exception e) when (e is ArgumentException or CryptographicException)
            {
                key.Dispose();
            }
        }

        throw Unreadable(path, "is not an RSA or ECDSA private key");
    }

    /// <summary>The certificates other than the leaf, each issuer after the certificate it signed; unrelated ones go last.</summary>
    private static List<X509Certificate2> IssuerOrder(X509Certificate2 leaf, List<X509Certificate2> certs)
    {
        var rest = certs.Where(c => c.Thumbprint != leaf.Thumbprint).DistinctBy(c => c.Thumbprint).ToList();
        var ordered = new List<X509Certificate2>();
        for (var current = leaf; rest.FirstOrDefault(c => c.Subject == current.Issuer) is { } issuer; current = issuer)
        {
            ordered.Add(issuer);
            rest.Remove(issuer);
        }

        ordered.AddRange(rest);
        return ordered;
    }

    private static byte[] Read(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CertificateImportException("unreadable", $"Cannot read {Path.GetFileName(path)}: {e.Message}");
        }
    }

    private static CertificateImportException Unreadable(string path, string what) => new("unreadable", $"{Path.GetFileName(path)} {what}.");
}
