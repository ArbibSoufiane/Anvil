using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using ArbiPache.Core.Certificates;
using ArbiPache.Core.Elevation;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Sites;
using Microsoft.Extensions.Time.Testing;

namespace ArbiPache.Core.Tests.Certificates;

public sealed class CertImporterTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ap-import-" + Guid.NewGuid().ToString("N")));
    private readonly string _inbox;
    private readonly CertificateStore _store;
    private readonly CertImporter _importer;

    public CertImporterTests()
    {
        _paths.EnsureCreated();
        _inbox = Path.Combine(_paths.Root, "inbox");
        Directory.CreateDirectory(_inbox);
        _store = new CertificateStore(_paths);
        _importer = new CertImporter(_store, new FakeTimeProvider(Now));
    }

    public void Dispose()
    {
        try { Directory.Delete(_paths.Root, recursive: true); } catch (IOException) { }
    }

    private sealed record Issued(X509Certificate2 Leaf, X509Certificate2 Intermediate, X509Certificate2 Root);

    private static X509Certificate2 Authority(string name, X509Certificate2? issuer, RSA key)
    {
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        if (issuer is null) return request.CreateSelfSigned(Now.AddYears(-1), Now.AddYears(5));
        using var signed = request.Create(issuer, Now.AddYears(-1), Now.AddYears(4), RandomNumberGenerator.GetBytes(8));
        return signed.CopyWithPrivateKey(key);
    }

    /// <summary>A leaf for <paramref name="names"/> issued by an intermediate under a root, valid until <paramref name="notAfter"/>.</summary>
    private static Issued Issue(DateTimeOffset notAfter, params string[] names)
    {
        using var rootKey = RSA.Create(2048);
        using var midKey = RSA.Create(2048);
        using var leafKey = RSA.Create(2048);
        var root = Authority("Client Root CA", null, rootKey);
        var intermediate = Authority("Client Issuing CA", root, midKey);
        var request = new CertificateRequest($"CN={names[0]}", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names) san.AddDnsName(name);
        request.CertificateExtensions.Add(san.Build());
        using var signed = request.Create(intermediate, Now.AddDays(-30), notAfter, RandomNumberGenerator.GetBytes(8));
        return new Issued(signed.CopyWithPrivateKey(leafKey), intermediate, root);
    }

    private string Pfx(Issued issued, string password)
    {
        // Root first on purpose: the importer orders the chain by issuer, not by file order.
        var collection = new X509Certificate2Collection
        {
            X509CertificateLoader.LoadCertificate(issued.Root.RawData),
            issued.Leaf,
            X509CertificateLoader.LoadCertificate(issued.Intermediate.RawData),
        };
        var path = Path.Combine(_inbox, "client.pfx");
        File.WriteAllBytes(path, collection.Export(X509ContentType.Pfx, password)!);
        return path;
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_inbox, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static string KeyPem(X509Certificate2 cert) => cert.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem();

    private static CertificateImportException Rejected(Action import) => Assert.Throws<CertificateImportException>(import);

    [Fact]
    public void Pfx_with_chain_imported()
    {
        var issued = Issue(Now.AddDays(90), "portal.clientco.com", "www.portal.clientco.com");
        var request = new CertImportRequest(Pfx(issued, "s3cret"), "s3cret", null, null, null);

        var saved = _importer.Import("portal.clientco.com", ["portal.clientco.com", "www.portal.clientco.com"], request);

        Assert.Equal(CertSource.Imported, saved.Source);
        Assert.Equal(["portal.clientco.com", "www.portal.clientco.com"], saved.Names);
        Assert.Equal(Path.Combine(_paths.Certs, "portal.clientco.com", "cert.pem"), saved.CertFile);
        using var cert = X509Certificate2.CreateFromPemFile(saved.CertFile, saved.KeyFile);
        Assert.Equal(issued.Leaf.Thumbprint, cert.Thumbprint);
        Assert.True(cert.HasPrivateKey);
        Assert.StartsWith("-----BEGIN PRIVATE KEY-----", File.ReadAllText(saved.KeyFile));
        var chain = new X509Certificate2Collection();
        chain.ImportFromPemFile(saved.ChainFile!);
        Assert.Equal([issued.Intermediate.Thumbprint, issued.Root.Thumbprint], chain.Select(c => c.Thumbprint));
    }

    [Fact]
    public void Pem_cert_key_and_chain_imported()
    {
        var issued = Issue(Now.AddDays(90), "shop.example.com");
        var request = new CertImportRequest(null, null, Write("shop.crt", issued.Leaf.ExportCertificatePem()), Write("shop.key", KeyPem(issued.Leaf)),
            Write("chain.pem", issued.Intermediate.ExportCertificatePem() + "\n" + issued.Root.ExportCertificatePem()));

        var saved = _importer.Import("shop.example.com", ["shop.example.com"], request);

        using var cert = X509Certificate2.CreateFromPemFile(saved.CertFile, saved.KeyFile);
        Assert.Equal(issued.Leaf.Thumbprint, cert.Thumbprint);
        var chain = new X509Certificate2Collection();
        chain.ImportFromPemFile(saved.ChainFile!);
        Assert.Equal([issued.Intermediate.Thumbprint, issued.Root.Thumbprint], chain.Select(c => c.Thumbprint));
    }

    [Fact]
    public void Full_chain_in_the_certificate_file_is_split()
    {
        var issued = Issue(Now.AddDays(90), "shop.example.com");
        var fullChain = issued.Leaf.ExportCertificatePem() + "\n" + issued.Intermediate.ExportCertificatePem();
        var request = new CertImportRequest(null, null, Write("fullchain.pem", fullChain), Write("shop.key", KeyPem(issued.Leaf)), null);

        var saved = _importer.Import("shop.example.com", ["shop.example.com"], request);

        using var cert = X509CertificateLoader.LoadCertificateFromFile(saved.CertFile);
        Assert.Equal(issued.Leaf.Thumbprint, cert.Thumbprint);
        var chain = new X509Certificate2Collection();
        chain.ImportFromPemFile(saved.ChainFile!);
        Assert.Equal([issued.Intermediate.Thumbprint], chain.Select(c => c.Thumbprint));
    }

    [Fact]
    public void Wrong_pfx_password_is_bad_password()
    {
        var issued = Issue(Now.AddDays(90), "portal.clientco.com");
        var request = new CertImportRequest(Pfx(issued, "s3cret"), "guess", null, null, null);

        var error = Rejected(() => _importer.Import("portal.clientco.com", ["portal.clientco.com"], request));

        Assert.Equal("bad-password", error.Code);
        Assert.Null(_store.Get("portal.clientco.com"));
    }

    [Fact]
    public void File_that_is_not_a_certificate_is_unreadable()
    {
        var request = new CertImportRequest(Write("notes.pfx", "hello"), "s3cret", null, null, null);

        Assert.Equal("unreadable", Rejected(() => _importer.Import("portal.clientco.com", ["portal.clientco.com"], request)).Code);
        var pem = new CertImportRequest(null, null, Write("notes.crt", "hello"), Write("notes.key", "hello"), null);
        Assert.Equal("unreadable", Rejected(() => _importer.Import("portal.clientco.com", ["portal.clientco.com"], pem)).Code);
    }

    [Fact]
    public void Mismatched_key_rejected()
    {
        var issued = Issue(Now.AddDays(90), "shop.example.com");
        var other = Issue(Now.AddDays(90), "shop.example.com");
        var request = new CertImportRequest(null, null, Write("shop.crt", issued.Leaf.ExportCertificatePem()), Write("other.key", KeyPem(other.Leaf)), null);

        var error = Rejected(() => _importer.Import("shop.example.com", ["shop.example.com"], request));

        Assert.Equal("key-mismatch", error.Code);
        Assert.Null(_store.Get("shop.example.com"));
    }

    [Fact]
    public void Expired_rejected()
    {
        var issued = Issue(Now.AddDays(-1), "shop.example.com");
        var request = new CertImportRequest(null, null, Write("shop.crt", issued.Leaf.ExportCertificatePem()), Write("shop.key", KeyPem(issued.Leaf)), null);

        var error = Rejected(() => _importer.Import("shop.example.com", ["shop.example.com"], request));

        Assert.Equal("expired", error.Code);
        Assert.Contains("2026-10-26", error.Message);
        Assert.Null(_store.Get("shop.example.com"));
    }

    [Fact]
    public void Wildcard_covers_one_level_only()
    {
        Assert.True(NameMatch.Covers(["*.a.com"], "x.a.com"));
        Assert.True(NameMatch.Covers(["*.A.com"], "X.a.COM"));
        Assert.False(NameMatch.Covers(["*.a.com"], "a.com"));
        Assert.False(NameMatch.Covers(["*.a.com"], "y.x.a.com"));
        Assert.False(NameMatch.Covers(["*.a.com"], "xa.com"));
        Assert.True(NameMatch.Covers(["a.com", "*.a.com"], "a.com"));
        Assert.True(NameMatch.Covers(["192.168.10.2"], "192.168.10.2"));
        Assert.False(NameMatch.Covers(["*.168.10.2"], "192.168.10.2"));
    }

    [Fact]
    public void Names_not_covered_rejected()
    {
        var issued = Issue(Now.AddDays(90), "shop.example.com");
        var request = new CertImportRequest(null, null, Write("shop.crt", issued.Leaf.ExportCertificatePem()), Write("shop.key", KeyPem(issued.Leaf)), null);

        var error = Rejected(() => _importer.Import("shop.example.com", ["shop.example.com", "www.shop.example.com"], request));

        Assert.Equal("names-not-covered", error.Code);
        Assert.Contains("www.shop.example.com", error.Message);
        Assert.Null(_store.Get("shop.example.com"));
    }

    [Fact]
    public void Key_file_is_readable_only_by_this_account_administrators_and_the_service()
    {
        var issued = Issue(Now.AddDays(90), "shop.example.com");
        var request = new CertImportRequest(null, null, Write("shop.crt", issued.Leaf.ExportCertificatePem()), Write("shop.key", KeyPem(issued.Leaf)), null);

        var saved = _importer.Import("shop.example.com", ["shop.example.com"], request);

        var security = new FileInfo(saved.KeyFile).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var allowed = new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), AclPlanner.ServiceSid };
        var holders = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(r => (SecurityIdentifier)r.IdentityReference).Distinct().ToList();
        Assert.Equal(allowed.OrderBy(s => s.Value), holders.OrderBy(s => s.Value));
    }
}
