using System.Net;
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

public sealed class LocalCaTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ap-ca-" + Guid.NewGuid().ToString("N")));
    private readonly FakeTimeProvider _time = new(Now);

    public LocalCaTests() => _paths.EnsureCreated();

    public void Dispose()
    {
        try { Directory.Delete(_paths.Root, recursive: true); } catch (IOException) { }
    }

    private LocalCa Ca() => new(_paths, new CertificateStore(_paths), _time);

    private string CaFile => Path.Combine(_paths.Certs, "ca", "ca.crt");

    private static X509Certificate2 Load(string file) => X509CertificateLoader.LoadCertificateFromFile(file);

    [Fact]
    public void Ca_created_once()
    {
        Ca().EnsureCa();
        var first = File.ReadAllText(CaFile);

        Ca().EnsureCa();
        Ca().Issue("shop.test", ["shop.test"]);

        Assert.Equal(first, File.ReadAllText(CaFile));
        using var ca = Load(CaFile);
        Assert.Equal($"CN=ArbiPache Local CA ({Environment.MachineName})", ca.Subject);
        Assert.True(Assert.Single(ca.Extensions.OfType<X509BasicConstraintsExtension>()).CertificateAuthority);
        Assert.IsAssignableFrom<ECDsa>(ca.GetECDsaPublicKey());
        Assert.Equal(Now.AddYears(10).UtcDateTime, ca.NotAfter.ToUniversalTime(), TimeSpan.FromMinutes(10));
        Assert.True(File.Exists(Path.Combine(_paths.Certs, "ca", "ca.key")));
    }

    // The CA key signs certificates any site on this account would trust: other local accounts must not read it.
    [Fact]
    public void Ca_key_is_limited_to_arbipache()
    {
        Ca().EnsureCa();

        var allowed = new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), AclPlanner.ServiceSid };
        foreach (FileSystemSecurity security in new FileSystemSecurity[]
        {
            new DirectoryInfo(Path.Combine(_paths.Certs, "ca")).GetAccessControl(),
            new FileInfo(Path.Combine(_paths.Certs, "ca", "ca.key")).GetAccessControl(),
        })
        {
            Assert.True(security.AreAccessRulesProtected);
            var holders = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Select(r => (SecurityIdentifier)r.IdentityReference).Distinct().ToList();
            Assert.Equal(allowed.OrderBy(s => s.Value), holders.OrderBy(s => s.Value));
        }
    }

    [Fact]
    public void Leaf_chains_to_local_ca()
    {
        var issued = Ca().Issue("shop.test", ["shop.test"]);

        using var leaf = Load(issued.CertFile);
        using var ca = Load(CaFile);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = Now.AddDays(1).UtcDateTime;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));

        Assert.True(chain.Build(leaf), string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation)));
        Assert.Equal(CertSource.LocalDev, issued.Source);
        Assert.Equal(File.ReadAllText(CaFile), File.ReadAllText(issued.ChainFile!));
        using var withKey = X509Certificate2.CreateFromPemFile(issued.CertFile, issued.KeyFile);
        Assert.True(withKey.HasPrivateKey);
    }

    [Fact]
    public void Leaf_san_contains_all_names()
    {
        var issued = Ca().Issue("shop.test", ["shop.test", "www.shop.test", "192.168.10.2"]);

        using var leaf = Load(issued.CertFile);
        var san = Assert.Single(leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>());
        Assert.Equal(["shop.test", "www.shop.test"], san.EnumerateDnsNames());
        Assert.Equal([IPAddress.Parse("192.168.10.2")], san.EnumerateIPAddresses());
        Assert.Equal(["shop.test", "www.shop.test", "192.168.10.2"], issued.Names);
        Assert.False(Assert.Single(leaf.Extensions.OfType<X509BasicConstraintsExtension>()).CertificateAuthority);
        Assert.Equal("1.3.6.1.5.5.7.3.1", Assert.Single(Assert.Single(leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>()).EnhancedKeyUsages.Cast<Oid>()).Value);
    }

    [Fact]
    public void Leaf_valid_825_days()
    {
        var issued = Ca().Issue("shop.test", ["shop.test"]);

        using var leaf = Load(issued.CertFile);
        Assert.Equal(TimeSpan.FromDays(825), leaf.NotAfter - leaf.NotBefore);
        Assert.True(leaf.NotBefore.ToUniversalTime() <= Now.UtcDateTime);
        Assert.Equal(new DateTimeOffset(leaf.NotAfter.ToUniversalTime()), issued.NotAfter);
        Assert.IsAssignableFrom<ECDsa>(leaf.GetECDsaPublicKey());
    }

    [Fact]
    public void New_ca_is_not_trusted_until_trust_is_called() // Trust itself prompts the user and is a manual checkpoint.
    {
        Assert.False(Ca().IsTrusted());

        Ca().EnsureCa();

        Assert.False(Ca().IsTrusted());
    }

    [Fact]
    public void Reissue_replaces_the_site_certificate()
    {
        var first = Ca().Issue("shop.test", ["shop.test"]);
        var firstPem = File.ReadAllText(first.CertFile);

        var second = Ca().Issue("shop.test", ["shop.test", "api.shop.test"]);

        Assert.NotEqual(firstPem, File.ReadAllText(second.CertFile));
        Assert.Equal(["shop.test", "api.shop.test"], new CertificateStore(_paths).Get("shop.test")!.Names);
    }
}
