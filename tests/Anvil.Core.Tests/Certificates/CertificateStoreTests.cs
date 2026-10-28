using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Anvil.Core.Certificates;
using Anvil.Core.Paths;
using Anvil.Core.Sites;

namespace Anvil.Core.Tests.Certificates;

public sealed class CertificateStoreTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ap-certs-" + Guid.NewGuid().ToString("N")));

    public CertificateStoreTests() => _paths.EnsureCreated();

    public void Dispose()
    {
        try { Directory.Delete(_paths.Root, recursive: true); } catch (IOException) { }
    }

    private static (string Cert, string Key, DateTimeOffset NotAfter) SelfSigned(params string[] names)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={names[0]}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names) san.AddDnsName(name);
        request.CertificateExtensions.Add(san.Build());
        var notAfter = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(90), TimeSpan.Zero);
        using var cert = request.CreateSelfSigned(notAfter.AddDays(-100), notAfter);
        return (cert.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem(), notAfter);
    }

    [Fact]
    public void CertificateStore_round_trip()
    {
        var (cert, key, notAfter) = SelfSigned("shop.example.com", "www.shop.example.com");
        var store = new CertificateStore(_paths);

        store.Save("shop.example.com", cert, key, chainPem: null, CertSource.Imported);

        var saved = new CertificateStore(_paths).Get("shop.example.com");
        Assert.NotNull(saved);
        Assert.Equal(CertSource.Imported, saved.Source);
        Assert.Equal(["shop.example.com", "www.shop.example.com"], saved.Names);
        Assert.Equal(notAfter, saved.NotAfter);
        Assert.Equal(Path.Combine(_paths.Certs, "shop.example.com", "cert.pem"), saved.CertFile);
        Assert.Equal(cert, File.ReadAllText(saved.CertFile));
        Assert.Equal(key, File.ReadAllText(saved.KeyFile));
        Assert.Null(saved.ChainFile);
        Assert.Equal(["shop.example.com"], store.All().Keys);
    }

    [Fact]
    public void Chain_is_kept_and_delete_removes_everything()
    {
        var (cert, key, _) = SelfSigned("portal.clientco.com");
        var (chain, _, _) = SelfSigned("ca.clientco.com");
        var store = new CertificateStore(_paths);

        store.Save("portal.clientco.com", cert, key, chain, CertSource.Imported);
        Assert.Equal(chain, File.ReadAllText(store.Get("portal.clientco.com")!.ChainFile!));

        store.Delete("portal.clientco.com");

        Assert.Null(store.Get("portal.clientco.com"));
        Assert.False(Directory.Exists(Path.Combine(_paths.Certs, "portal.clientco.com")));
        Assert.Empty(store.All());
    }
}
