using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Anvil.Agent;
using Anvil.Core.Agent;
using Anvil.Core.Apache;
using Anvil.Core.Certificates;
using Anvil.Core.Components;
using Anvil.Core.Elevation;
using Anvil.Core.Hosts;
using Anvil.Core.Paths;
using Anvil.Core.Preflight;
using Anvil.Core.Settings;
using Anvil.Core.Sites;
using Microsoft.Extensions.Time.Testing;

namespace Anvil.Core.Tests.Agent;

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

    private async Task<Orchestrator> StartedAsync(TimeProvider? time = null)
    {
        var orchestrator = new Orchestrator(_paths, new SettingsStore(_paths), _tester, _apache, _ => [],
            new ComponentCatalog([], new ShippedCatalogProvider(_paths)),
            new ComponentInstaller(_paths, new Downloader(new HttpClient()), _ => null), _pools, _ => true, hostsText: () => _hosts,
            legacyHome: LegacyHome, time: time);
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

        var result = await orchestrator.SaveSiteAsync(Site("shop.example.com"), Ct);

        Assert.True(result.Ok, result.Error);
        Assert.True(File.Exists(SiteJson("shop.example.com")));
        Assert.Contains("ServerName shop.example.com", File.ReadAllText(SiteVhost("shop.example.com")));
        Assert.Equal(1, _apache.Restarts);
        Assert.Equal("shop.example.com", Assert.Single(orchestrator.GetSites()).Id);
    }

    [Fact]
    public async Task Failing_config_test_leaves_sites_dir_unchanged()
    {
        var orchestrator = await StartedAsync();
        await orchestrator.SaveSiteAsync(Site("shop.example.com"), Ct);
        var before = SitesDir();
        var vhostBefore = File.ReadAllText(SiteVhost("shop.example.com"));
        _tester.Next = new ConfigTestResult(false, "AH00526: Syntax error");

        var added = await orchestrator.SaveSiteAsync(Site("blog.test"), Ct);
        var edited = await orchestrator.SaveSiteAsync(Site("shop.example.com") with { Aliases = ["www.shop.example.com"] }, Ct);

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

        var result = await orchestrator.SaveSiteAsync(Site("shop.example.com") with { Name = "shop" }, Ct);

        Assert.False(result.Ok);
        Assert.Contains("Enter a domain", result.Error);
        Assert.False(Directory.Exists(_paths.Sites) && Directory.GetFiles(_paths.Sites).Length > 0);
    }

    [Fact]
    public async Task Validate_reports_uninstalled_php()
    {
        var orchestrator = await StartedAsync();
        TestPhp.Install(_paths, "8.5.12");

        var errors = orchestrator.ValidateSite(Site("shop.example.com") with { PhpVersion = "7.4.33" });

        Assert.Equal(["PhpVersion"], errors.Select(e => e.Field));
        Assert.Empty(orchestrator.ValidateSite(Site("shop.example.com") with { PhpVersion = "8.5.12" }));
    }

    [Fact]
    public async Task Validate_refuses_a_folder_that_holds_anvil()
    {
        var orchestrator = await StartedAsync();

        var errors = orchestrator.ValidateSite(Site("shop.example.com") with { Root = _paths.Root });

        Assert.Equal(["Root"], errors.Select(e => e.Field));
    }

    [Fact]
    public async Task New_test_domain_requests_hosts_update()
    {
        var orchestrator = await StartedAsync();

        var result = await orchestrator.SaveSiteAsync(Site("blog.test") with { Aliases = ["www.blog.test"] }, Ct);
        var second = await orchestrator.SaveSiteAsync(Site("shop.example.com") with { AddToHosts = true }, Ct);

        Assert.Equal(["blog.test", "www.blog.test"], result.HostsDomains);
        Assert.Equal(["blog.test", "www.blog.test", "shop.example.com"], second.HostsDomains);
    }

    [Fact]
    public async Task Unchanged_hosts_list_returns_null_hosts()
    {
        _hosts = HostsFile.ApplyBlock(_hosts, ["blog.test"]);
        var orchestrator = await StartedAsync();

        var test = await orchestrator.SaveSiteAsync(Site("blog.test"), Ct);
        var publicSite = await orchestrator.SaveSiteAsync(Site("shop.example.com"), Ct);

        Assert.True(test.Ok, test.Error);
        Assert.Null(test.HostsDomains);
        Assert.Null(publicSite.HostsDomains);
    }

    [Fact]
    public async Task LocalDev_site_gets_certificate()
    {
        var orchestrator = await StartedAsync();

        var result = await orchestrator.SaveSiteAsync(Site("dev.test") with { Cert = CertSource.LocalDev, Aliases = ["www.dev.test"] }, Ct);

        Assert.True(result.Ok, result.Error);
        var cert = new CertificateStore(_paths).Get("dev.test");
        Assert.NotNull(cert);
        Assert.Equal(CertSource.LocalDev, cert.Source);
        Assert.Equal(["dev.test", "www.dev.test"], cert.Names);
        Assert.Contains("SSLEngine on", File.ReadAllText(SiteVhost("dev.test")));
    }

    [Fact]
    public async Task Remove_deletes_json_cert_and_vhost()
    {
        var orchestrator = await StartedAsync();
        await orchestrator.SaveSiteAsync(Site("dev.test") with { Cert = CertSource.LocalDev }, Ct);

        var result = await orchestrator.RemoveSiteAsync("dev.test", Ct);

        Assert.True(result.Ok, result.Error);
        Assert.False(File.Exists(SiteJson("dev.test")));
        Assert.False(File.Exists(SiteVhost("dev.test")));
        Assert.Null(new CertificateStore(_paths).Get("dev.test"));
        Assert.False((await orchestrator.RemoveSiteAsync("dev.test", Ct)).Ok);
    }

    [Fact]
    public async Task Rejected_remove_keeps_the_site()
    {
        var orchestrator = await StartedAsync();
        await orchestrator.SaveSiteAsync(Site("shop.example.com"), Ct);
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
        await orchestrator.SaveSiteAsync(Site("shop.example.com"), Ct);

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
        await orchestrator.SaveSiteAsync(old, Ct);

        var result = await orchestrator.SaveSiteAsync(old with { Name = "new.test" }, Ct);

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

        var result = await orchestrator.SaveSiteAsync(Site("legacy.test") with { PhpVersion = "8.4.27" }, Ct);

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
        await orchestrator.SaveSiteAsync(Site("shop.test"), Ct);
        await orchestrator.SaveSiteAsync(Site("blog.test") with { PhpVersion = "8.4.27" }, Ct);

        var php = orchestrator.GetPhp();

        Assert.Equal(["htdocs", "shop.test"], php.Single(p => p.Version == "8.5.12").UsedBy);
        Assert.Equal(["blog.test"], php.Single(p => p.Version == "8.4.27").UsedBy);
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
        Assert.Null((await orchestrator.SaveSiteAsync(Site("shop.test"), Ct)).HostsDomains);

        _hosts += "# >>> ApacheManager >>>\r\n127.0.0.1\tshop.test\r\n# <<< ApacheManager <<<\r\n";

        Assert.Equal(["shop.test"], (await orchestrator.SaveSiteAsync(Site("shop.test"), Ct)).HostsDomains);
    }

    [Fact]
    public async Task Reissue_replaces_every_local_dev_certificate()
    {
        var orchestrator = await StartedAsync();
        await orchestrator.StartAsync(null, Ct);
        await orchestrator.SaveSiteAsync(Site("shop.test") with { Cert = CertSource.LocalDev }, Ct);
        await orchestrator.SaveSiteAsync(Site("plain.test"), Ct);
        var before = File.ReadAllText(new CertificateStore(_paths).Get("shop.test")!.CertFile);
        var restarts = _apache.Restarts;

        var result = await orchestrator.ReissueLocalCertificatesAsync(Ct);

        Assert.True(result.Ok, result.Error);
        Assert.NotEqual(before, File.ReadAllText(new CertificateStore(_paths).Get("shop.test")!.CertFile));
        Assert.Null(new CertificateStore(_paths).Get("plain.test"));
        Assert.Equal(restarts + 1, _apache.Restarts);
    }

    [Fact]
    public async Task Local_certificates_near_expiry_are_reissued_at_startup()
    {
        var site = Site("shop.test") with { Cert = CertSource.LocalDev };
        new SiteStore(_paths).Save(site);
        var (cert, key) = SelfSigned(["shop.test"], DateTimeOffset.UtcNow.AddDays(10));
        new CertificateStore(_paths).Save("shop.test", cert, key, null, CertSource.LocalDev);
        var before = new CertificateStore(_paths).Get("shop.test")!.NotAfter;

        await StartedAsync();

        var after = new CertificateStore(_paths).Get("shop.test")!;
        Assert.True(after.NotAfter > before.AddDays(30), $"{after.NotAfter} is not later than {before}");
        Assert.Equal(CertSource.LocalDev, after.Source);
    }

    [Fact]
    public async Task Local_certificates_far_from_expiry_are_kept_at_startup()
    {
        new SiteStore(_paths).Save(Site("shop.test") with { Cert = CertSource.LocalDev });
        var (cert, key) = SelfSigned(["shop.test"], DateTimeOffset.UtcNow.AddDays(200));
        new CertificateStore(_paths).Save("shop.test", cert, key, null, CertSource.LocalDev);
        var before = File.ReadAllText(new CertificateStore(_paths).Get("shop.test")!.CertFile);

        await StartedAsync();

        Assert.Equal(before, File.ReadAllText(new CertificateStore(_paths).Get("shop.test")!.CertFile));
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

    [Theory]
    [InlineData(FileShare.None)] // nothing can even read it
    [InlineData(FileShare.Read)] // an editor has it open: the folder cannot move
    public async Task Locked_conf_folder_rolls_the_site_back(FileShare share)
    {
        var o = await StartedAsync();
        Assert.True((await o.SaveSiteAsync(Site("shop.test"), Ct)).Ok);
        var catchAll = File.ReadAllText(Path.Combine(_paths.Vhosts, "00-catchall.conf"));

        ApplyResult r;
        using (new FileStream(Path.Combine(_paths.Vhosts, "00-catchall.conf"), FileMode.Open, FileAccess.Read, share))
            r = await o.SaveSiteAsync(Site("new.test"), Ct);

        Assert.False(r.Ok);
        Assert.StartsWith("Anvil could not write the Apache configuration (", r.Error);
        Assert.EndsWith("). Nothing was changed.", r.Error);
        Assert.False(File.Exists(Path.Combine(_paths.Sites, SiteSet.IdFor(SiteKind.Domain, "new.test") + ".json")));
        Assert.Equal(catchAll, File.ReadAllText(Path.Combine(_paths.Vhosts, "00-catchall.conf")));
        Assert.True((await o.SaveSiteAsync(Site("new.test"), Ct)).Ok);
    }
}
