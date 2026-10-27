using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ArbiPache.Agent;
using ArbiPache.Core.Agent;
using ArbiPache.Core.Apache;
using ArbiPache.Core.Certificates;
using ArbiPache.Core.Components;
using ArbiPache.Core.Elevation;
using ArbiPache.Core.Hosts;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Preflight;
using ArbiPache.Core.Settings;
using ArbiPache.Core.Sites;
using Microsoft.Extensions.Time.Testing;

namespace ArbiPache.Core.Tests.Agent;

public sealed class SiteOperationsTests : IDisposable
{
    private const string Version = "2.4.69";

    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ap-siteops-" + Guid.NewGuid().ToString("N")));
    private readonly FakeTester _tester = new();
    private readonly List<string> _events = [];
    private readonly FakeApache _apache;
    private readonly FakePools _pools;
    private string _hosts = "127.0.0.1 router\r\n";

    public SiteOperationsTests()
    {
        _apache = new FakeApache(_events);
        _pools = new FakePools(_events);
        var runtime = _paths.ApacheRuntime(Version);
        Directory.CreateDirectory(Path.Combine(runtime, "conf"));
        Directory.CreateDirectory(Path.Combine(runtime, "bin"));
        File.WriteAllText(Path.Combine(runtime, "conf", "httpd.conf"), "ServerRoot \"c:/Apache24\"\r\nListen 80\r\n");
        File.WriteAllText(Path.Combine(runtime, "bin", "httpd.exe"), "");
        Directory.CreateDirectory(_paths.Data);
        new SettingsStore(_paths).Save(new AppSettings { ActiveApache = Version, StartServicesOnLaunch = false });
    }

    public void Dispose()
    {
        try { Directory.Delete(_paths.Root, recursive: true); } catch (IOException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Orchestrator> StartedAsync(Func<bool, IAcmeProtocol>? acme = null, TimeProvider? time = null, bool runningAsService = false)
    {
        var orchestrator = new Orchestrator(_paths, new SettingsStore(_paths), _tester, _apache, _ => [],
            new ComponentCatalog([], new ShippedCatalogProvider(_paths)),
            new ComponentInstaller(_paths, new Downloader(new HttpClient()), _ => null), _pools, _ => true, runningAsService, hostsText: () => _hosts,
            legacyHome: LegacyHome, acme: acme, time: time);
        await orchestrator.InitializeAsync(Ct);
        return orchestrator;
    }

    private SiteDefinition Site(string name, SiteKind kind = SiteKind.Domain)
    {
        var root = Path.Combine(_paths.Root, "projects", name);
        Directory.CreateDirectory(root);
        return new SiteDefinition { Id = SiteSet.IdFor(kind, name), Kind = kind, Type = SiteType.Php, Name = name, Root = root };
    }

    private string LegacyHome => Path.Combine(_paths.Root, "v3");

    private void V3Site(string domain, string phpVersion = "php-8.4.23-Win32-vs17-x64")
    {
        var root = Path.Combine(_paths.Root, "projects", domain);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(LegacyHome, "sites"));
        File.WriteAllText(Path.Combine(LegacyHome, "sites", domain + ".json"), JsonSerializer.Serialize(new
        {
            domain, type = "php", root, php_version = phpVersion, index = new[] { "index.php" }, https = true, http_redirect = true,
            aliases = Array.Empty<string>(), proxy_target = (string?)null, enabled = true, created = "2026-10-25T14:19:16",
        }));
    }

    private string SiteJson(string id) => Path.Combine(_paths.Sites, id + ".json");

    private string SiteVhost(string id) => Path.Combine(_paths.Vhosts, $"site-{id}.conf");

    private Dictionary<string, byte[]> SitesDir() =>
        Directory.GetFiles(_paths.Sites).ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes);

    [Fact]
    public async Task Saved_site_gets_json_and_vhost_and_reloads_apache()
    {
        var orchestrator = await StartedAsync();
        await orchestrator.StartAsync(null, Ct);

        var result = await orchestrator.SaveSiteAsync(Site("shop.example.com"), null, Ct);

        Assert.True(result.Ok, result.Error);
        Assert.True(File.Exists(SiteJson("shop.example.com")));
        Assert.Contains("ServerName shop.example.com", File.ReadAllText(SiteVhost("shop.example.com")));
        Assert.Equal(1, _apache.Restarts);
        Assert.Equal("shop.example.com", Assert.Single(orchestrator.GetSites()).Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Only_the_service_asks_for_access_to_a_site_folder_outside_the_app_folder(bool runningAsService)
    {
        var orchestrator = await StartedAsync(runningAsService: runningAsService);
        // As after installing the service: the app folder, data and runtime are granted.
        foreach (var grant in AclPlanner.Plan(_paths, [])) Assert.Null(FolderPermissions.Grant(grant.Path, AclPlanner.ServiceSid, grant.Right));
        var outside = _paths.Root + "-sites";
        var root = Directory.CreateDirectory(Path.Combine(outside, "shop")).FullName;
        try
        {
            var result = await orchestrator.SaveSiteAsync(Site("shop.example.com") with { Root = root }, null, Ct);

            Assert.True(result.Ok, result.Error);
            if (runningAsService) Assert.Equal([new AclGrant(root, AclRight.Read)], result.RequiredGrants!);
            else Assert.Null(result.RequiredGrants);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task Failing_config_test_leaves_sites_dir_unchanged()
    {
        var orchestrator = await StartedAsync();
        await orchestrator.SaveSiteAsync(Site("shop.example.com"), null, Ct);
        var before = SitesDir();
        var vhostBefore = File.ReadAllText(SiteVhost("shop.example.com"));
        _tester.Next = new ConfigTestResult(false, "AH00526: Syntax error");

        var added = await orchestrator.SaveSiteAsync(Site("blog.test"), null, Ct);
        var edited = await orchestrator.SaveSiteAsync(Site("shop.example.com") with { Aliases = ["www.shop.example.com"] }, null, Ct);

        Assert.False(added.Ok);
        Assert.False(edited.Ok);
        Assert.Contains("AH00526", added.ConfigTestOutput);
        Assert.Equal(before, SitesDir());
        Assert.False(File.Exists(SiteVhost("blog.test")));
        Assert.Equal(vhostBefore, File.ReadAllText(SiteVhost("shop.example.com")));
    }

    [Fact]
    public async Task Invalid_site_is_refused_with_its_errors()
    {
        var orchestrator = await StartedAsync();

        var result = await orchestrator.SaveSiteAsync(Site("shop.example.com") with { Access = AccessLevel.SpecificIps }, null, Ct);

        Assert.False(result.Ok);
        Assert.Contains("allowed IP", result.Error);
        Assert.False(Directory.Exists(_paths.Sites) && Directory.GetFiles(_paths.Sites).Length > 0);
    }

    [Fact]
    public async Task Validate_reports_uninstalled_php()
    {
        var orchestrator = await StartedAsync();
        TestPhp.Install(_paths, "8.5.12");

        var errors = orchestrator.ValidateSite(Site("shop.example.com") with
        {
            PhpVersion = "7.4.33",
            Mounts = [new SiteMount("/demo", _paths.Htdocs, SiteType.Php, "8.1.34")],
        });

        Assert.Equal(["PhpVersion", "Mounts"], errors.Select(e => e.Field));
        Assert.Empty(orchestrator.ValidateSite(Site("shop.example.com") with { PhpVersion = "8.5.12" }));
    }

    [Fact]
    public async Task New_test_domain_requests_hosts_update()
    {
        var orchestrator = await StartedAsync();

        var result = await orchestrator.SaveSiteAsync(Site("blog.test") with { Aliases = ["www.blog.test"] }, null, Ct);
        var second = await orchestrator.SaveSiteAsync(Site("shop.example.com") with { AddToHosts = true }, null, Ct);

        Assert.Equal(["blog.test", "www.blog.test"], result.HostsDomains);
        Assert.Equal(["blog.test", "www.blog.test", "shop.example.com"], second.HostsDomains);
    }

    [Fact]
    public async Task Unchanged_hosts_list_returns_null_hosts()
    {
        _hosts = HostsFile.ApplyBlock(_hosts, ["blog.test"]);
        var orchestrator = await StartedAsync();

        var test = await orchestrator.SaveSiteAsync(Site("blog.test"), null, Ct);
        var publicSite = await orchestrator.SaveSiteAsync(Site("shop.example.com"), null, Ct);

        Assert.True(test.Ok, test.Error);
        Assert.Null(test.HostsDomains);
        Assert.Null(publicSite.HostsDomains);
    }

    [Fact]
    public async Task LocalDev_site_gets_certificate()
    {
        var orchestrator = await StartedAsync();

        var result = await orchestrator.SaveSiteAsync(Site("dev.test") with { Cert = CertSource.LocalDev, Aliases = ["www.dev.test"] }, null, Ct);

        Assert.True(result.Ok, result.Error);
        var cert = new CertificateStore(_paths).Get("dev.test");
        Assert.NotNull(cert);
        Assert.Equal(CertSource.LocalDev, cert.Source);
        Assert.Equal(["dev.test", "www.dev.test"], cert.Names);
        Assert.Contains("SSLEngine on", File.ReadAllText(SiteVhost("dev.test")));
    }

    [Fact]
    public async Task Password_needs_secrets_once_and_writes_htpasswd()
    {
        var orchestrator = await StartedAsync();
        var site = Site("shop.example.com") with { PasswordProtected = true };

        var missing = await orchestrator.SaveSiteAsync(site, null, Ct);
        var saved = await orchestrator.SaveSiteAsync(site, new SiteSecrets("client", "s3cret"), Ct);
        var resaved = await orchestrator.SaveSiteAsync(site with { Aliases = ["www.shop.example.com"] }, null, Ct);

        Assert.False(missing.Ok);
        Assert.True(saved.Ok, saved.Error);
        Assert.True(resaved.Ok, resaved.Error);
        Assert.True(HtpasswdFile.Verify(Path.Combine(_paths.Auth, "shop.example.com.htpasswd"), "client", "s3cret"));
        Assert.Contains("Require valid-user", File.ReadAllText(SiteVhost("shop.example.com")));
    }

    [Fact]
    public async Task Remove_deletes_json_cert_and_vhost()
    {
        var orchestrator = await StartedAsync();
        await orchestrator.SaveSiteAsync(Site("dev.test") with { Cert = CertSource.LocalDev, PasswordProtected = true }, new SiteSecrets("me", "pw"), Ct);

        var result = await orchestrator.RemoveSiteAsync("dev.test", Ct);

        Assert.True(result.Ok, result.Error);
        Assert.False(File.Exists(SiteJson("dev.test")));
        Assert.False(File.Exists(SiteVhost("dev.test")));
        Assert.Null(new CertificateStore(_paths).Get("dev.test"));
        Assert.False(File.Exists(Path.Combine(_paths.Auth, "dev.test.htpasswd")));
        Assert.False((await orchestrator.RemoveSiteAsync("dev.test", Ct)).Ok);
    }

    [Fact]
    public async Task Rejected_remove_keeps_the_site()
    {
        var orchestrator = await StartedAsync();
        await orchestrator.SaveSiteAsync(Site("shop.example.com"), null, Ct);
        _tester.Next = new ConfigTestResult(false, "AH00526");

        var result = await orchestrator.RemoveSiteAsync("shop.example.com", Ct);

        Assert.False(result.Ok);
        Assert.True(File.Exists(SiteJson("shop.example.com")));
        Assert.True(File.Exists(SiteVhost("shop.example.com")));
    }

    [Fact]
    public async Task Disabled_site_keeps_json_and_loses_vhost()
    {
        var orchestrator = await StartedAsync();
        await orchestrator.SaveSiteAsync(Site("shop.example.com"), null, Ct);

        var result = await orchestrator.SetSiteEnabledAsync("shop.example.com", false, Ct);

        Assert.True(result.Ok, result.Error);
        Assert.False(Assert.Single(orchestrator.GetSites()).Enabled);
        Assert.False(File.Exists(SiteVhost("shop.example.com")));
    }

    [Fact]
    public async Task Renaming_a_site_moves_its_files()
    {
        var orchestrator = await StartedAsync();
        var old = Site("old.test");
        await orchestrator.SaveSiteAsync(old, null, Ct);

        var result = await orchestrator.SaveSiteAsync(old with { Name = "new.test" }, null, Ct);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(["new.test"], orchestrator.GetSites().Select(s => s.Id));
        Assert.False(File.Exists(SiteVhost("old.test")));
        Assert.True(File.Exists(SiteVhost("new.test")));
    }

    [Fact]
    public async Task Site_php_version_gets_its_own_pool()
    {
        TestPhp.Install(_paths, "8.5.12");
        TestPhp.Install(_paths, "8.4.27");
        var store = new SettingsStore(_paths);
        store.Save(store.Load() with { DefaultPhp = "8.5.12" });
        var orchestrator = await StartedAsync();
        await orchestrator.StartAsync(null, Ct);

        var result = await orchestrator.SaveSiteAsync(Site("legacy.test") with { PhpVersion = "8.4.27" }, null, Ct);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(["php85", "php84"], _pools.Running.Select(p => p.Key).Order().Reverse());
        Assert.Contains($"proxy:fcgi://127.0.0.1:{_pools.Running.Single(p => p.Key == "php84").Port}/", File.ReadAllText(SiteVhost("legacy.test")));
    }

    [Fact]
    public async Task Php_versions_list_the_sites_using_them()
    {
        TestPhp.Install(_paths, "8.5.12");
        TestPhp.Install(_paths, "8.4.27");
        var store = new SettingsStore(_paths);
        store.Save(store.Load() with { DefaultPhp = "8.5.12" });
        var orchestrator = await StartedAsync();
        await orchestrator.SaveSiteAsync(Site("shop.test"), null, Ct);
        await orchestrator.SaveSiteAsync(Site("192.168.10.2", SiteKind.Ip) with
        {
            PhpVersion = "8.4.27", Mounts = [new SiteMount("/demo", _paths.Htdocs, SiteType.Php, "8.5.12")],
        }, null, Ct);

        var php = orchestrator.GetPhp();

        Assert.Equal(["htdocs", "192.168.10.2", "shop.test"], php.Single(p => p.Version == "8.5.12").UsedBy);
        Assert.Equal(["192.168.10.2"], php.Single(p => p.Version == "8.4.27").UsedBy);
    }

    [Fact]
    public async Task Copy_into_htdocs_copies_and_refuses_existing_names()
    {
        var orchestrator = await StartedAsync();
        var source = Path.Combine(_paths.Root, "projects", "blog");
        Directory.CreateDirectory(Path.Combine(source, "css"));
        File.WriteAllText(Path.Combine(source, "index.php"), "<?php echo 1;");
        File.WriteAllText(Path.Combine(source, "css", "site.css"), "body{}");

        var copied = await orchestrator.CopyIntoHtdocsAsync(source, "blog", Ct);
        var again = await orchestrator.CopyIntoHtdocsAsync(source, "blog", Ct);
        var badName = await orchestrator.CopyIntoHtdocsAsync(source, "..\\outside", Ct);

        Assert.True(copied.Ok, copied.Error);
        Assert.Equal("body{}", File.ReadAllText(Path.Combine(_paths.Htdocs, "blog", "css", "site.css")));
        Assert.False(again.Ok);
        Assert.False(badName.Ok);
        Assert.Contains(orchestrator.GetHtdocs(), e => e.Folder == "blog");
    }

    [Fact]
    public async Task Import_saves_v3_sites_with_certificates_and_asks_for_hosts()
    {
        TestPhp.Install(_paths, "8.4.23");
        V3Site("afaq.test");
        V3Site("old.test", "php-7.4.9-Win32-vc15-x64");
        var orchestrator = await StartedAsync();

        var report = await orchestrator.ImportLegacyAsync(Ct);

        Assert.True(report.Result?.Ok, report.Result?.Error);
        Assert.Equal(["afaq.test", "old.test"], report.Imported);
        Assert.Equal(["7.4.9"], report.MissingPhpVersions);
        Assert.Equal(["afaq.test", "old.test"], orchestrator.GetSites().Select(s => s.Id).Order());
        Assert.True(File.Exists(SiteVhost("afaq.test")));
        Assert.False(File.Exists(SiteVhost("old.test")));
        Assert.Equal(CertSource.LocalDev, new CertificateStore(_paths).Get("afaq.test")?.Source);
        Assert.Equal(["afaq.test"], report.Result!.HostsDomains);
    }

    [Fact]
    public async Task Import_rejected_by_apache_saves_nothing()
    {
        TestPhp.Install(_paths, "8.4.23");
        V3Site("afaq.test");
        var orchestrator = await StartedAsync();
        _tester.Next = new ConfigTestResult(false, "AH00526: Syntax error");

        var report = await orchestrator.ImportLegacyAsync(Ct);

        Assert.False(report.Result?.Ok);
        Assert.Empty(report.Imported);
        Assert.Empty(orchestrator.GetSites());
        Assert.False(File.Exists(SiteJson("afaq.test")));
    }

    [Fact]
    public async Task V3_hosts_lines_for_a_site_ask_for_a_hosts_update()
    {
        _hosts = HostsFile.ApplyBlock(_hosts, ["shop.test"]);
        var orchestrator = await StartedAsync();
        Assert.Null((await orchestrator.SaveSiteAsync(Site("shop.test"), null, Ct)).HostsDomains);

        _hosts += "# >>> ApacheManager >>>\r\n127.0.0.1\tshop.test\r\n# <<< ApacheManager <<<\r\n";

        Assert.Equal(["shop.test"], (await orchestrator.SaveSiteAsync(Site("shop.test"), null, Ct)).HostsDomains);
    }

    [Fact]
    public async Task Reissue_replaces_every_local_dev_certificate()
    {
        var orchestrator = await StartedAsync();
        await orchestrator.StartAsync(null, Ct);
        await orchestrator.SaveSiteAsync(Site("shop.test") with { Cert = CertSource.LocalDev }, null, Ct);
        await orchestrator.SaveSiteAsync(Site("plain.test"), null, Ct);
        var before = File.ReadAllText(new CertificateStore(_paths).Get("shop.test")!.CertFile);
        var restarts = _apache.Restarts;

        var result = await orchestrator.ReissueLocalCertificatesAsync(Ct);

        Assert.True(result.Ok, result.Error);
        Assert.NotEqual(before, File.ReadAllText(new CertificateStore(_paths).Get("shop.test")!.CertFile));
        Assert.Null(new CertificateStore(_paths).Get("plain.test"));
        Assert.Equal(restarts + 1, _apache.Restarts);
    }

    private sealed class FakeAcme
    {
        public List<bool> Staging { get; } = [];
        public IReadOnlyList<string> Names { get; private set; } = [];
        public bool Fails { get; init; }

        public IAcmeProtocol For(bool staging)
        {
            Staging.Add(staging);
            return new Protocol(this);
        }

        private sealed class Protocol(FakeAcme owner) : IAcmeProtocol, IAcmeOrder
        {
            public IReadOnlyList<(string Token, string KeyAuthorization)> Http01Challenges => [("tok", "tok.thumb")];

            public Task<IAcmeOrder> NewOrderAsync(IReadOnlyList<string> names, CancellationToken ct)
            {
                owner.Names = names;
                return Task.FromResult<IAcmeOrder>(this);
            }

            public Task ValidateAsync(CancellationToken ct) => owner.Fails
                ? Task.FromException(new InvalidOperationException("Let's Encrypt could not verify shop.example.com: connection refused."))
                : Task.CompletedTask;

            public Task<(string CertPem, string ChainPem, string KeyPem)> FinalizeAsync(CancellationToken ct)
            {
                var (cert, key) = SelfSigned(owner.Names, DateTimeOffset.UtcNow.AddDays(90));
                return Task.FromResult((cert, "", key));
            }
        }
    }

    private static (string Cert, string Key) SelfSigned(IReadOnlyList<string> names, DateTimeOffset notAfter)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={names[0]}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names) san.AddDnsName(name);
        request.CertificateExtensions.Add(san.Build());
        using var cert = request.CreateSelfSigned(notAfter.AddDays(-60), notAfter);
        return (cert.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }

    [Fact]
    public async Task Renewal_orders_missing_letsencrypt_certificate_and_restarts()
    {
        var acme = new FakeAcme();
        var orchestrator = await StartedAsync(acme.For);
        await orchestrator.StartAsync(null, Ct);
        await orchestrator.SaveSiteAsync(Site("shop.example.com") with { Cert = CertSource.LetsEncrypt, Aliases = ["www.shop.example.com"] }, null, Ct);
        var restarts = _apache.Restarts;

        var report = await orchestrator.RenewCertificatesAsync(Ct);

        Assert.Equal(["shop.example.com"], report.Renewed);
        Assert.Empty(report.Failed);
        Assert.Equal([false], acme.Staging);
        Assert.Equal(["shop.example.com", "www.shop.example.com"], acme.Names);
        Assert.Equal(CertSource.LetsEncrypt, new CertificateStore(_paths).Get("shop.example.com")!.Source);
        Assert.Contains("SSLCertificateFile", File.ReadAllText(SiteVhost("shop.example.com")));
        Assert.Equal(restarts + 1, _apache.Restarts);
    }

    [Fact]
    public async Task Renewal_reissues_local_dev_certificate_close_to_expiry()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var orchestrator = await StartedAsync(time: time);
        await orchestrator.SaveSiteAsync(Site("shop.test") with { Cert = CertSource.LocalDev }, null, Ct);
        var before = File.ReadAllText(new CertificateStore(_paths).Get("shop.test")!.CertFile);
        Assert.Empty((await orchestrator.RenewCertificatesAsync(Ct)).Renewed);
        time.Advance(TimeSpan.FromDays(815));

        var report = await orchestrator.RenewCertificatesAsync(Ct);

        Assert.Equal(["shop.test"], report.Renewed);
        Assert.NotEqual(before, File.ReadAllText(new CertificateStore(_paths).Get("shop.test")!.CertFile));
    }

    [Fact]
    public async Task Imported_certificate_near_expiry_becomes_a_finding()
    {
        var (cert, key) = SelfSigned(["portal.clientco.com"], DateTimeOffset.UtcNow.AddDays(5).AddHours(1));
        new CertificateStore(_paths).Save("portal.clientco.com", cert, key, null, CertSource.Imported);
        var orchestrator = await StartedAsync();
        await orchestrator.SaveSiteAsync(Site("portal.clientco.com") with { Cert = CertSource.Imported }, null, Ct);

        var report = await orchestrator.RenewCertificatesAsync(Ct);

        Assert.Empty(report.Renewed);
        var finding = Assert.Single(orchestrator.Status().Findings, f => f.Id == "cert-portal.clientco.com");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("portal.clientco.com expires in 5 days", finding.Message);
    }

    [Fact]
    public async Task Expiry_finding_clears_once_a_renewed_certificate_is_in_place()
    {
        var store = new CertificateStore(_paths);
        var (cert, key) = SelfSigned(["portal.clientco.com"], DateTimeOffset.UtcNow.AddDays(5).AddHours(1));
        store.Save("portal.clientco.com", cert, key, null, CertSource.Imported);
        var orchestrator = await StartedAsync();
        var site = Site("portal.clientco.com") with { Cert = CertSource.Imported };
        await orchestrator.SaveSiteAsync(site, null, Ct);
        Assert.Contains(orchestrator.Status().Findings, f => f.Id == "cert-portal.clientco.com");

        (cert, key) = SelfSigned(["portal.clientco.com"], DateTimeOffset.UtcNow.AddDays(365));
        store.Save("portal.clientco.com", cert, key, null, CertSource.Imported);
        await orchestrator.SaveSiteAsync(site, null, Ct);

        Assert.DoesNotContain(orchestrator.Status().Findings, f => f.Id == "cert-portal.clientco.com");
    }

    [Fact]
    public async Task Failed_renewal_becomes_a_finding_and_restarts_nothing()
    {
        var acme = new FakeAcme { Fails = true };
        var orchestrator = await StartedAsync(acme.For);
        await orchestrator.StartAsync(null, Ct);
        await orchestrator.SaveSiteAsync(Site("shop.example.com") with { Cert = CertSource.LetsEncrypt }, null, Ct);
        var restarts = _apache.Restarts;

        var report = await orchestrator.RenewCertificatesAsync(Ct);

        Assert.Equal(["shop.example.com"], report.Failed);
        var finding = Assert.Single(orchestrator.Status().Findings, f => f.Id == "cert-shop.example.com");
        Assert.Contains("connection refused", finding.Message);
        Assert.Contains("6 hours", finding.Fix);
        Assert.Null(new CertificateStore(_paths).Get("shop.example.com"));
        Assert.Equal(restarts, _apache.Restarts);
    }

    private CertImportRequest PemFiles(string name, DateTimeOffset notAfter, string? keyFrom = null)
    {
        var (cert, key) = SelfSigned([name], notAfter);
        if (keyFrom is not null) key = SelfSigned([keyFrom], notAfter).Key;
        var dir = Path.Combine(_paths.Root, "inbox");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "site.crt"), cert);
        File.WriteAllText(Path.Combine(dir, "site.key"), key);
        return new CertImportRequest(null, null, Path.Combine(dir, "site.crt"), Path.Combine(dir, "site.key"), null);
    }

    [Fact]
    public async Task Imported_certificate_is_applied_to_the_site()
    {
        var orchestrator = await StartedAsync();
        await orchestrator.StartAsync(null, Ct);
        await orchestrator.SaveSiteAsync(Site("portal.clientco.com") with { Cert = CertSource.Imported }, null, Ct);
        Assert.DoesNotContain("SSLCertificateFile", File.ReadAllText(SiteVhost("portal.clientco.com")));
        var restarts = _apache.Restarts;

        var saved = await orchestrator.ImportCertificateAsync("portal.clientco.com", PemFiles("portal.clientco.com", DateTimeOffset.UtcNow.AddDays(200)), Ct);

        Assert.Equal(CertSource.Imported, saved.Source);
        Assert.Contains("SSLCertificateFile", File.ReadAllText(SiteVhost("portal.clientco.com")));
        Assert.Equal(restarts + 1, _apache.Restarts);
    }

    [Fact]
    public async Task Import_with_the_wrong_key_keeps_the_site_as_it_was()
    {
        var orchestrator = await StartedAsync();
        await orchestrator.SaveSiteAsync(Site("portal.clientco.com") with { Cert = CertSource.Imported }, null, Ct);
        var vhost = File.ReadAllText(SiteVhost("portal.clientco.com"));

        var error = await Assert.ThrowsAsync<CertificateImportException>(() => orchestrator.ImportCertificateAsync("portal.clientco.com",
            PemFiles("portal.clientco.com", DateTimeOffset.UtcNow.AddDays(200), keyFrom: "other.example.com"), Ct));

        Assert.Equal("key-mismatch", error.Code);
        Assert.Null(new CertificateStore(_paths).Get("portal.clientco.com"));
        Assert.Equal(vhost, File.ReadAllText(SiteVhost("portal.clientco.com")));
    }

    [Fact]
    public async Task Certificate_for_a_missing_site_is_refused()
    {
        var orchestrator = await StartedAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.ImportCertificateAsync("nope.example.com", PemFiles("nope.example.com", DateTimeOffset.UtcNow.AddDays(200)), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.IssueLetsEncryptAsync("nope.example.com", staging: false, Ct));
    }

    [Fact]
    public async Task Lets_encrypt_certificate_is_issued_on_request()
    {
        var acme = new FakeAcme();
        var orchestrator = await StartedAsync(acme.For);
        await orchestrator.StartAsync(null, Ct);
        await orchestrator.SaveSiteAsync(Site("shop.example.com") with { Cert = CertSource.LetsEncrypt }, null, Ct);
        var restarts = _apache.Restarts;

        var saved = await orchestrator.IssueLetsEncryptAsync("shop.example.com", staging: true, Ct);

        Assert.Equal(CertSource.LetsEncrypt, saved.Source);
        Assert.Equal([true], acme.Staging);
        Assert.Contains("SSLCertificateFile", File.ReadAllText(SiteVhost("shop.example.com")));
        Assert.Equal(restarts + 1, _apache.Restarts);
        Assert.Equal(CertSource.LetsEncrypt, orchestrator.GetCertificates()["shop.example.com"].Source);
    }
}
