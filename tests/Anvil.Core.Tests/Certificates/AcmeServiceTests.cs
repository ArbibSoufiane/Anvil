using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Anvil.Core.Certificates;
using Anvil.Core.Paths;
using Anvil.Core.Sites;

namespace Anvil.Core.Tests.Certificates;

public sealed class AcmeServiceTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ap-acme-" + Guid.NewGuid().ToString("N")));
    private readonly CertificateStore _store;
    private readonly List<bool> _stagingRequests = [];
    private FakeOrder _order = new([("tok-1", "tok-1.thumbprint"), ("tok-2", "tok-2.thumbprint")]);

    public AcmeServiceTests()
    {
        _paths.EnsureCreated();
        _store = new CertificateStore(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_paths.Root, recursive: true); } catch (IOException) { }
    }

    private string ChallengeDir => Path.Combine(_paths.Acme, ".well-known", "acme-challenge");

    private AcmeService Service() => new(staging =>
    {
        _stagingRequests.Add(staging);
        return new FakeProtocol(_order);
    }, _store, _paths);

    private sealed class FakeProtocol(FakeOrder order) : IAcmeProtocol
    {
        public Task<IAcmeOrder> NewOrderAsync(IReadOnlyList<string> names, CancellationToken ct)
        {
            order.Names = names;
            return Task.FromResult<IAcmeOrder>(order);
        }
    }

    private sealed class FakeOrder(IReadOnlyList<(string Token, string KeyAuthorization)> challenges) : IAcmeOrder
    {
        public IReadOnlyList<string> Names { get; set; } = [];
        public Dictionary<string, string?> SeenDuringValidation { get; } = [];
        public string? ChallengeDir { get; set; }
        public Exception? ValidationFails { get; init; }
        public IReadOnlyList<(string Token, string KeyAuthorization)> Http01Challenges => challenges;

        public Task ValidateAsync(CancellationToken ct)
        {
            foreach (var (token, _) in challenges)
            {
                var file = Path.Combine(ChallengeDir!, token);
                SeenDuringValidation[token] = File.Exists(file) ? File.ReadAllText(file) : null;
            }

            return ValidationFails is null ? Task.CompletedTask : Task.FromException(ValidationFails);
        }

        public Task<(string CertPem, string ChainPem, string KeyPem)> FinalizeAsync(CancellationToken ct)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest($"CN={Names[0]}", key, HashAlgorithmName.SHA256);
            var san = new SubjectAlternativeNameBuilder();
            foreach (var name in Names) san.AddDnsName(name);
            request.CertificateExtensions.Add(san.Build());
            using var leaf = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(90));
            using var issuerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var issuer = new CertificateRequest("CN=(STAGING) Fake Issuer", issuerKey, HashAlgorithmName.SHA256)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow.AddYears(1));
            return Task.FromResult((leaf.ExportCertificatePem(), issuer.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem()));
        }
    }

    [Fact]
    public async Task Challenge_file_written_then_removed()
    {
        _order.ChallengeDir = ChallengeDir;

        await Service().IssueAsync("shop.example.com", ["shop.example.com", "www.shop.example.com"], staging: false, CancellationToken.None);

        Assert.Equal("tok-1.thumbprint", _order.SeenDuringValidation["tok-1"]);
        Assert.Equal("tok-2.thumbprint", _order.SeenDuringValidation["tok-2"]);
        Assert.Empty(Directory.GetFiles(ChallengeDir));
    }

    [Fact]
    public async Task Challenge_files_removed_when_validation_fails()
    {
        _order = new FakeOrder([("tok-1", "tok-1.thumbprint")]) { ChallengeDir = ChallengeDir, ValidationFails = new InvalidOperationException("unreachable") };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service().IssueAsync("shop.example.com", ["shop.example.com"], staging: false, CancellationToken.None));

        Assert.Equal("tok-1.thumbprint", _order.SeenDuringValidation["tok-1"]);
        Assert.Empty(Directory.GetFiles(ChallengeDir));
        Assert.Null(_store.Get("shop.example.com"));
    }

    [Fact]
    public async Task Unsafe_token_is_refused()
    {
        _order = new FakeOrder([("..\\..\\sites\\x.json", "evil")]) { ChallengeDir = ChallengeDir };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service().IssueAsync("shop.example.com", ["shop.example.com"], staging: false, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(_paths.Sites, "x.json")));
        Assert.Empty(_order.SeenDuringValidation);
    }

    [Fact]
    public async Task Issued_cert_saved_as_letsencrypt()
    {
        _order.ChallengeDir = ChallengeDir;

        var saved = await Service().IssueAsync("shop.example.com", ["shop.example.com", "www.shop.example.com"], staging: false, CancellationToken.None);

        Assert.Equal(["shop.example.com", "www.shop.example.com"], _order.Names);
        Assert.Equal(CertSource.LetsEncrypt, saved.Source);
        Assert.Equal(["shop.example.com", "www.shop.example.com"], saved.Names);
        Assert.Equal(CertSource.LetsEncrypt, _store.Get("shop.example.com")!.Source);
        using var cert = X509Certificate2.CreateFromPemFile(saved.CertFile, saved.KeyFile);
        Assert.True(cert.HasPrivateKey);
        Assert.Contains("BEGIN CERTIFICATE", File.ReadAllText(saved.ChainFile!));
    }

    [Fact]
    public async Task Staging_uses_staging_account()
    {
        _order.ChallengeDir = ChallengeDir;

        await Service().IssueAsync("shop.example.com", ["shop.example.com"], staging: true, CancellationToken.None);
        await Service().IssueAsync("shop.example.com", ["shop.example.com"], staging: false, CancellationToken.None);

        Assert.Equal([true, false], _stagingRequests);
        Assert.Equal(Path.Combine(_paths.Certs, "acme", "account-staging.pem"), CertesAcmeProtocol.AccountKeyFile(_paths, staging: true));
        Assert.Equal(Path.Combine(_paths.Certs, "acme", "account-production.pem"), CertesAcmeProtocol.AccountKeyFile(_paths, staging: false));
        Assert.Equal(Certes.Acme.WellKnownServers.LetsEncryptStagingV2, CertesAcmeProtocol.DirectoryFor(staging: true));
        Assert.Equal(Certes.Acme.WellKnownServers.LetsEncryptV2, CertesAcmeProtocol.DirectoryFor(staging: false));
    }

    [Fact]
    public async Task Dns_check_resolves_names_and_reports_none_for_unknown_ones()
    {
        var local = await DnsCheck.ResolveAsync("localhost", CancellationToken.None);
        var unknown = await DnsCheck.ResolveAsync("no-such-host.invalid", CancellationToken.None);

        Assert.Contains(local, a => a is "127.0.0.1" or "::1");
        Assert.Empty(unknown);
    }
}
