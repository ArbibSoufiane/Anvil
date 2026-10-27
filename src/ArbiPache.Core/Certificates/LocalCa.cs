using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ArbiPache.Core.IO;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Sites;

namespace ArbiPache.Core.Certificates;

/// <summary>
/// ArbiPache's own certificate authority for development HTTPS, built with CertificateRequest (no mkcert). The CA lives in
/// data\certs\ca; site certificates go to the <see cref="CertificateStore"/>. Trust and Untrust change CurrentUser\Root,
/// so they run in the GUI process where Windows can show its confirmation prompt.
/// </summary>
public sealed class LocalCa(AppPaths paths, CertificateStore store, TimeProvider time)
{
    private const string ServerAuth = "1.3.6.1.5.5.7.3.1";
    private const int LeafDays = 825;
    private static readonly TimeSpan Backdate = TimeSpan.FromMinutes(5);

    private string Dir => Path.Combine(paths.Certs, "ca");

    public string CertFile => Path.Combine(Dir, "ca.crt");

    private string KeyFile => Path.Combine(Dir, "ca.key");

    public void EnsureCa()
    {
        if (File.Exists(CertFile) && File.Exists(KeyFile)) return;

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=ArbiPache Local CA ({Environment.MachineName})", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var now = time.GetUtcNow();
        using var ca = request.CreateSelfSigned(now - Backdate, now.AddYears(10));

        Directory.CreateDirectory(Dir);
        AtomicFile.WriteAllText(KeyFile, key.ExportPkcs8PrivateKeyPem());
        AtomicFile.WriteAllText(CertFile, ca.ExportCertificatePem());
    }

    /// <summary>Issues (or replaces) the site's certificate for <paramref name="names"/>: DNS names and IP addresses.</summary>
    public SiteCertificate Issue(string siteId, IReadOnlyList<string> names)
    {
        if (names.Count == 0) throw new ArgumentException("A certificate needs at least one name.", nameof(names));
        EnsureCa();
        using var ca = X509Certificate2.CreateFromPemFile(CertFile, KeyFile);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName(names[0]);
        var request = new CertificateRequest(subject.Build(), key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
        {
            if (IPAddress.TryParse(name, out var ip)) san.AddIpAddress(ip);
            else san.AddDnsName(name);
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(ServerAuth) }, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));

        // A leaf cannot outlive its CA.
        var notBefore = time.GetUtcNow() - Backdate;
        var notAfter = notBefore.AddDays(LeafDays);
        if (notAfter > ca.NotAfter) notAfter = ca.NotAfter;
        using var leaf = request.Create(ca, notBefore, notAfter, SerialNumber());

        store.Save(siteId, leaf.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem(), File.ReadAllText(CertFile), CertSource.LocalDev);
        return store.Get(siteId)!;
    }

    public bool IsTrusted()
    {
        if (!File.Exists(CertFile)) return false;
        using var ca = X509CertificateLoader.LoadCertificateFromFile(CertFile);
        using var root = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        root.Open(OpenFlags.ReadOnly);
        return root.Certificates.Find(X509FindType.FindByThumbprint, ca.Thumbprint, validOnly: false).Count > 0;
    }

    /// <summary>Adds the CA to CurrentUser\Root. Windows asks the user to confirm.</summary>
    public void Trust()
    {
        EnsureCa();
        using var ca = X509CertificateLoader.LoadCertificateFromFile(CertFile);
        using var root = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        root.Open(OpenFlags.ReadWrite);
        root.Add(ca);
    }

    public void Untrust()
    {
        if (!File.Exists(CertFile)) return;
        using var ca = X509CertificateLoader.LoadCertificateFromFile(CertFile);
        using var root = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        root.Open(OpenFlags.ReadWrite);
        root.RemoveRange(root.Certificates.Find(X509FindType.FindByThumbprint, ca.Thumbprint, validOnly: false));
    }

    private static byte[] SerialNumber()
    {
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        return serial;
    }
}
