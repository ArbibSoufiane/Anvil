using System.Text.RegularExpressions;
using ArbiPache.Core.Apache;
using ArbiPache.Core.Certificates;
using ArbiPache.Core.Modes;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Php;
using ArbiPache.Core.Settings;
using ArbiPache.Core.Sites;
using ArbiPache.Core.Tests.Support;

namespace ArbiPache.Core.Tests.Apache;

public sealed class VhostRenderTests
{
    private static readonly AppPaths Paths = new(@"D:\ArbiPache");
    private static readonly StockConfState Stock = new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<int>([80]));
    private static readonly PhpPool Php85 = Pool("8.5.12", 9100);
    private static readonly PhpPool Php84 = Pool("8.4.27", 9101);

    private static PhpPool Pool(string version, int port) =>
        new(version, PhpVersionKey.Of(version), port, 4, 1000, $@"D:\ArbiPache\runtime\php\{version}\php-cgi.exe",
            $@"D:\ArbiPache\data\php\{version}", $@"D:\ArbiPache\data\php\{version}\conf.d");

    private static SiteDefinition Domain(string name = "shop.example.com") => new()
    {
        Id = SiteSet.IdFor(SiteKind.Domain, name),
        Kind = SiteKind.Domain,
        Type = SiteType.Php,
        Name = name,
        Root = @"D:\Sites\shop\public",
    };

    private static SiteDefinition Ip(string address) => new()
    {
        Id = SiteSet.IdFor(SiteKind.Ip, address),
        Kind = SiteKind.Ip,
        Type = SiteType.Php,
        Name = address,
        Root = @"D:\Sites\shop\public",
    };

    private static SiteCertificate Cert(string id, bool chain = false) =>
        new(CertSource.Imported, [id], DateTimeOffset.UtcNow.AddDays(90), $@"D:\ArbiPache\data\certs\{id}\cert.pem",
            $@"D:\ArbiPache\data\certs\{id}\key.pem", chain ? $@"D:\ArbiPache\data\certs\{id}\chain.pem" : null);

    private static RenderedConfig RenderConfig(
        IReadOnlyList<SiteDefinition> sites, ServerMode mode = ServerMode.Development, IReadOnlyList<HtdocsEntry>? htdocs = null,
        IReadOnlyDictionary<string, SiteCertificate>? certs = null) =>
        new ConfigRenderer(Paths).Render(new RenderInput
        {
            Settings = new AppSettings { Mode = mode, DefaultPhp = "8.5.12" },
            Stock = Stock,
            Profile = BuiltInProfiles.For(mode),
            Pools = [Php85, Php84],
            Sites = sites,
            Htdocs = htdocs ?? [],
            Certificates = certs ?? new Dictionary<string, SiteCertificate>(),
        });

    private static IReadOnlyDictionary<string, string> Render(
        IReadOnlyList<SiteDefinition> sites, ServerMode mode = ServerMode.Development, IReadOnlyList<HtdocsEntry>? htdocs = null,
        IReadOnlyDictionary<string, SiteCertificate>? certs = null) =>
        RenderConfig(sites, mode, htdocs, certs).Files.ToDictionary(f => f.Key, f => f.Value.ReplaceLineEndings("\n"));

    private static string SiteFile(string id) => $@"vhosts\site-{id}.conf";

    [Fact]
    public void Domain_https_with_redirect()
    {
        var site = Domain() with { Cert = CertSource.Imported };

        var files = Render([site], certs: new Dictionary<string, SiteCertificate> { [site.Id] = Cert(site.Id, chain: true) });

        Snapshot.Match(files[SiteFile(site.Id)], "site-domain-https");
    }

    [Fact]
    public void Https_redirect_can_be_turned_off()
    {
        var site = Domain() with { Cert = CertSource.Imported, HttpRedirect = false };

        var conf = Render([site], certs: new Dictionary<string, SiteCertificate> { [site.Id] = Cert(site.Id) })[SiteFile(site.Id)];

        Assert.DoesNotContain("RedirectMatch", conf);
        Assert.Equal(2, Regex.Matches(conf, "DocumentRoot \"D:/Sites/shop/public\"").Count);
        Assert.DoesNotContain("SSLCertificateChainFile", conf);
    }

    [Fact]
    public void Https_without_certificate_yet_serves_http_only()
    {
        var site = Domain() with { Cert = CertSource.LetsEncrypt };

        var conf = Render([site])[SiteFile(site.Id)];

        Assert.DoesNotContain("*:443", conf);
        Assert.DoesNotContain("RedirectMatch", conf);
        Assert.Contains("DocumentRoot \"D:/Sites/shop/public\"", conf);
    }

    [Fact]
    public void Domain_with_aliases()
    {
        var site = Domain() with { Aliases = ["www.shop.example.com", "Bücher.example.com"] };

        var conf = Render([site])[SiteFile(site.Id)];

        Assert.Contains("    ServerName shop.example.com\n    ServerAlias www.shop.example.com xn--bcher-kva.example.com\n", conf);
    }

    [Fact]
    public void Ip_site_with_demo_mount()
    {
        var site = Ip("192.168.10.2") with
        {
            Access = AccessLevel.Network,
            Mounts =
            [
                new SiteMount("/demo", @"D:\Sites\shop-demo\public", SiteType.Php, "8.4.27"),
                new SiteMount("/docs", @"D:\Sites\docs", SiteType.Static, null),
            ],
        };

        Snapshot.Match(Render([site])[SiteFile("ip-192-168-10-2")], "site-ip-mount");
    }

    [Fact]
    public void Php_mount_gets_fallback_resource()
    {
        var site = Domain() with
        {
            Mounts =
            [
                new SiteMount("/demo", @"D:\Sites\demo", SiteType.Php, null),
                new SiteMount("/docs", @"D:\Sites\docs", SiteType.Static, null),
            ],
        };

        var conf = Render([site])[SiteFile(site.Id)];

        Assert.Contains("    Alias \"/demo\" \"D:/Sites/demo\"\n", conf);
        Assert.Contains("        FallbackResource /demo/index.php\n", conf);
        Assert.DoesNotContain("FallbackResource /docs", conf);
    }

    [Fact]
    public void Ipv6_site_matches_by_alias_without_brackets()
    {
        var site = Ip("2001:db8::10") with { Cert = CertSource.Imported };

        var conf = Render([site], certs: new Dictionary<string, SiteCertificate> { [site.Id] = Cert(site.Id) })[SiteFile("ip-2001-db8--10")];

        // Apache reads "[" in ServerName as a wildcard and matches IPv6 hosts without brackets.
        Assert.Contains("    ServerName ip-2001-db8--10.invalid\n    ServerAlias 2001:db8::10\n", conf);
        Assert.Contains(" https://[2001:db8::10]/$1\n", conf);
    }

    [Fact]
    public void Localhost_alias_matches_ipv6_loopback() =>
        Assert.Contains("    ServerAlias 127.0.0.1 ::1\n", Render([])[@"vhosts\01-localhost.conf"]);

    [Theory]
    [InlineData(ServerMode.Development)]
    [InlineData(ServerMode.Production)]
    public void All_addresses_ip_site_replaces_catchall(ServerMode mode)
    {
        var files = Render([Ip("*") with { Root = @"D:\Sites\kiosk" }], mode);

        var catchall = files[@"vhosts\00-catchall.conf"];
        Assert.Contains("    ServerName arbipache-catchall.invalid\n", catchall);
        Assert.Contains("    DocumentRoot \"D:/Sites/kiosk\"\n", catchall);
        Assert.DoesNotContain("D:/ArbiPache/htdocs", catchall);
        Assert.DoesNotContain("Require all denied", catchall);
        Assert.DoesNotContain(SiteFile("ip-all"), files.Keys);
        Assert.Contains("D:/ArbiPache/htdocs", files[@"vhosts\01-localhost.conf"]);
    }

    [Fact]
    public void Static_site_has_no_php_handler()
    {
        var conf = Render([Domain() with { Type = SiteType.Static, Index = ["index.html"] }])[SiteFile("shop.example.com")];

        Assert.DoesNotContain("proxy:fcgi", conf);
        Assert.DoesNotContain("FilesMatch", conf);
        Assert.Contains("        DirectoryIndex index.html\n", conf);
    }

    [Fact]
    public void Php_site_on_a_version_with_no_pool_denies_php_files()
    {
        var conf = Render([Domain() with { PhpVersion = "7.4.33" }])[SiteFile("shop.example.com")];

        Assert.DoesNotContain("proxy:fcgi", conf);
        Assert.Contains("        <FilesMatch \"\\.php$\">\n            Require all denied\n        </FilesMatch>\n", conf);
    }

    [Fact]
    public void Site_php_version_picks_its_pool()
    {
        var files = Render([Domain() with { PhpVersion = "8.4.27" }, Domain("blog.test")]);

        Assert.Contains("proxy:fcgi://127.0.0.1:9101/", files[SiteFile("shop.example.com")]);
        Assert.Contains("proxy:fcgi://127.0.0.1:9100/", files[SiteFile("blog.test")]);
    }

    [Fact]
    public void Proxy_site_upgrades_websockets()
    {
        var site = Domain("app.test") with
        {
            Type = SiteType.Proxy,
            Root = "http://127.0.0.1:5173",
            Access = AccessLevel.ThisPc,
            Mounts = [new SiteMount("/files", @"D:\Sites\files", SiteType.Static, null)],
        };

        Snapshot.Match(Render([site])[SiteFile("app.test")], "site-proxy");
    }

    [Fact]
    public void Https_proxy_target_upgrades_to_wss()
    {
        var conf = Render([Domain("app.test") with { Type = SiteType.Proxy, Root = "https://localhost:8443/" }])[SiteFile("app.test")];

        Assert.Contains("    RewriteRule /(.*) wss://localhost:8443/$1 [P,L]\n", conf);
        Assert.Contains("    ProxyPass / https://localhost:8443/\n", conf);
    }

    [Fact]
    public void Load_balancer_site_trusts_only_listed_proxies()
    {
        var site = Domain() with
        {
            Cert = CertSource.LoadBalancer,
            TrustedProxies = ["10.0.0.5", "10.0.1.0/24"],
            AcceptOnlyFromProxies = true,
        };

        Snapshot.Match(Render([site])[SiteFile(site.Id)], "site-lb");
    }

    [Fact]
    public void Disabled_site_not_rendered()
    {
        var files = Render([Domain() with { Enabled = false }, Ip("*") with { Enabled = false }]);

        Assert.DoesNotContain(SiteFile("shop.example.com"), files.Keys);
        Assert.Contains("D:/ArbiPache/htdocs", files[@"vhosts\00-catchall.conf"]);
    }

    [Fact]
    public void Paths_with_spaces_and_unicode_render_quoted()
    {
        var site = Domain() with
        {
            Root = @"D:\Mes Sites\café\public",
            Mounts = [new SiteMount("/demo", @"D:\Mes Sites\démo", SiteType.Php, null)],
        };

        var conf = Render([site])[SiteFile(site.Id)];

        Assert.Contains("    DocumentRoot \"D:/Mes Sites/café/public\"\n    <Directory \"D:/Mes Sites/café/public\">\n", conf);
        Assert.Contains("    Alias \"/demo\" \"D:/Mes Sites/démo\"\n    <Directory \"D:/Mes Sites/démo\">\n", conf);
    }

    [Fact]
    public void Password_protected_site_uses_its_htpasswd_file()
    {
        var conf = Render([Domain() with { PasswordProtected = true }])[SiteFile("shop.example.com")];

        Assert.Contains("        AuthUserFile \"D:/ArbiPache/data/auth/shop.example.com.htpasswd\"\n", conf);
        Assert.Contains("            Require valid-user\n", conf);
    }

    [Fact]
    public void Site_logs_go_to_their_own_folder()
    {
        var config = RenderConfig([Domain()]);
        var conf = config.Files[SiteFile("shop.example.com")].ReplaceLineEndings("\n");

        Assert.Contains("    ErrorLog \"D:/ArbiPache/data/logs/sites/shop.example.com/error.log\"\n", conf);
        Assert.Contains("    CustomLog \"D:/ArbiPache/data/logs/sites/shop.example.com/access.log\" combined\n", conf);
        Assert.Contains(@"D:\ArbiPache\data\logs\sites\shop.example.com", config.Folders);
    }

    [Fact]
    public void Htdocs_alias_entries_render_in_localhost_and_catchall()
    {
        HtdocsEntry[] htdocs =
        [
            new("blog", @"D:\ArbiPache\htdocs\blog", false, null, AccessLevel.Network),
            new("pma", @"D:\Tools\phpMyAdmin", true, null, AccessLevel.ThisPc),
        ];

        var files = Render([], htdocs: htdocs);

        Snapshot.Match(files[@"vhosts\01-localhost.conf"] + "=== catch-all ===\n" + files[@"vhosts\00-catchall.conf"], "htdocs-entries");
    }

    [Fact]
    public void Htdocs_entry_with_own_php_and_ips_gets_directory_block()
    {
        HtdocsEntry[] htdocs = [new("legacy", @"D:\ArbiPache\htdocs\legacy", false, "8.4.27", AccessLevel.SpecificIps)];
        SiteDefinition[] saved =
        [
            new()
            {
                Id = "htdocs-legacy", Kind = SiteKind.Htdocs, Type = SiteType.Php, Name = "legacy", Root = @"D:\ArbiPache\htdocs\legacy",
                PhpVersion = "8.4.27", Access = AccessLevel.SpecificIps, AllowedIps = ["192.168.1.50"],
            },
        ];

        var files = Render(saved, htdocs: htdocs);

        var catchall = files[@"vhosts\00-catchall.conf"];
        Assert.Contains("    <Directory \"D:/ArbiPache/htdocs/legacy\">\n", catchall);
        Assert.Contains("        Require local\n        Require ip 192.168.1.50\n", catchall);
        Assert.Contains("proxy:fcgi://127.0.0.1:9101/", catchall);
        Assert.DoesNotContain("Alias \"/legacy\"", catchall);
        Assert.Contains("proxy:fcgi://127.0.0.1:9101/", files[@"vhosts\01-localhost.conf"]);
        Assert.DoesNotContain(SiteFile("htdocs-legacy"), files.Keys);
    }

    [Fact]
    public void Production_catchall_has_no_htdocs_entries()
    {
        var files = Render([], ServerMode.Production, [new("pma", @"D:\Tools\phpMyAdmin", true, null, AccessLevel.ThisPc)]);

        Assert.DoesNotContain("phpMyAdmin", files[@"vhosts\00-catchall.conf"]);
        Assert.Contains("Alias \"/pma\" \"D:/Tools/phpMyAdmin\"", files[@"vhosts\01-localhost.conf"]);
    }

    [Fact]
    public void No_ip_specific_virtualhost_anywhere()
    {
        var shop = Domain() with { Cert = CertSource.Imported, Aliases = ["www.shop.example.com"] };
        SiteDefinition[] sites =
        [
            shop,
            Ip("192.168.10.2") with { Mounts = [new SiteMount("/demo", @"D:\Sites\demo", SiteType.Php, null)] },
            Ip("2001:db8::10"),
            Ip("*"),
            Domain("app.test") with { Type = SiteType.Proxy, Root = "http://127.0.0.1:5173" },
            Domain("lb.example.com") with { Cert = CertSource.LoadBalancer, TrustedProxies = ["10.0.0.5"] },
        ];

        var files = Render(sites, certs: new Dictionary<string, SiteCertificate> { [shop.Id] = Cert(shop.Id) });

        Assert.All(files.Values, text => Assert.DoesNotMatch(@"<VirtualHost\s+(\d|\[)", text));
        Assert.All(files.Values, text => Assert.DoesNotMatch(@"(?m)^\s*Server(Name|Alias) .*\[", text));
        Assert.All(files.Values.Where(t => t.Contains("<VirtualHost", StringComparison.Ordinal)),
            text => Assert.All(Regex.Matches(text, "<VirtualHost [^>]*>"), m => Assert.Matches(@"^<VirtualHost \*:\d+>$", m.Value)));
    }
}
