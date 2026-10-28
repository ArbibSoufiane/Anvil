using System.Text.RegularExpressions;
using Anvil.Core.Apache;
using Anvil.Core.Certificates;
using Anvil.Core.Modes;
using Anvil.Core.Paths;
using Anvil.Core.Php;
using Anvil.Core.Settings;
using Anvil.Core.Sites;
using Anvil.Core.Tests.Support;

namespace Anvil.Core.Tests.Apache;

public sealed class VhostRenderTests
{
    private static readonly AppPaths Paths = new(@"D:\Anvil");
    private static readonly StockConfState Stock = new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<int>([80]));
    private static readonly PhpPool Php85 = Pool("8.5.12", 9100);
    private static readonly PhpPool Php84 = Pool("8.4.27", 9101);

    private static PhpPool Pool(string version, int port) =>
        new(version, PhpVersionKey.Of(version), port, 4, 1000, $@"D:\Anvil\runtime\php\{version}\php-cgi.exe",
            $@"D:\Anvil\data\php\{version}", $@"D:\Anvil\data\php\{version}\conf.d");

    private static SiteDefinition Domain(string name = "shop.example.com") => new()
    {
        Id = SiteSet.IdFor(SiteKind.Domain, name),
        Kind = SiteKind.Domain,
        Type = SiteType.Php,
        Name = name,
        Root = @"D:\Sites\shop\public",
    };

    private static SiteCertificate Cert(string id, bool chain = false) =>
        new(CertSource.LocalDev, [id], DateTimeOffset.UtcNow.AddDays(90), $@"D:\Anvil\data\certs\{id}\cert.pem",
            $@"D:\Anvil\data\certs\{id}\key.pem", chain ? $@"D:\Anvil\data\certs\{id}\chain.pem" : null);

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
        var site = Domain() with { Cert = CertSource.LocalDev };

        var files = Render([site], certs: new Dictionary<string, SiteCertificate> { [site.Id] = Cert(site.Id, chain: true) });

        Snapshot.Match(files[SiteFile(site.Id)], "site-domain-https");
    }

    [Fact]
    public void Https_redirect_can_be_turned_off()
    {
        var site = Domain() with { Cert = CertSource.LocalDev, HttpRedirect = false };

        var conf = Render([site], certs: new Dictionary<string, SiteCertificate> { [site.Id] = Cert(site.Id) })[SiteFile(site.Id)];

        Assert.DoesNotContain("RedirectMatch", conf);
        Assert.Equal(2, Regex.Matches(conf, "DocumentRoot \"D:/Sites/shop/public\"").Count);
        Assert.DoesNotContain("SSLCertificateChainFile", conf);
    }

    [Fact]
    public void Https_without_certificate_yet_serves_http_only()
    {
        var site = Domain() with { Cert = CertSource.LocalDev };

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
    public void Localhost_alias_matches_ipv6_loopback() =>
        Assert.Contains("    ServerAlias 127.0.0.1 ::1\n", Render([])[@"vhosts\01-localhost.conf"]);

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
    public void Disabled_site_not_rendered()
    {
        var files = Render([Domain() with { Enabled = false }]);

        Assert.DoesNotContain(SiteFile("shop.example.com"), files.Keys);
        Assert.Contains("D:/Anvil/htdocs", files[@"vhosts\00-catchall.conf"]);
    }

    [Fact]
    public void Paths_with_spaces_and_unicode_render_quoted()
    {
        var site = Domain() with { Root = @"D:\Mes Sites\café\public" };

        var conf = Render([site])[SiteFile(site.Id)];

        Assert.Contains("    DocumentRoot \"D:/Mes Sites/café/public\"\n    <Directory \"D:/Mes Sites/café/public\">\n", conf);
    }

    [Fact]
    public void Site_logs_go_to_their_own_folder()
    {
        var config = RenderConfig([Domain()]);
        var conf = config.Files[SiteFile("shop.example.com")].ReplaceLineEndings("\n");

        Assert.Contains("\\\"D:/Anvil/data/logs/sites/shop.example.com/error.%Y-%m-%d.log\\\" 86400\"\n", conf);
        Assert.Contains("""\"D:/Anvil/data/logs/sites/shop.example.com/access.%Y-%m-%d.log\" 86400" combined""", conf);
        Assert.Contains(@"D:\Anvil\data\logs\sites\shop.example.com", config.Folders);
    }

    // An app's .htaccess "Require all granted" replaces a <Directory>'s Require; a later <Location> with AuthMerging And
    // keeps the site's restriction on top of whatever the .htaccess says, including the app's own denies.
    [Fact]
    public void Htaccess_cannot_widen_a_restricted_site()
    {
        var site = Domain() with { Access = AccessLevel.ThisPc };

        var conf = Render([site])[SiteFile(site.Id)];

        Assert.Contains("    <Location \"/\">\n        AuthMerging And\n        Require local\n    </Location>\n", conf);
    }

    [Fact]
    public void Htaccess_cannot_open_a_network_site_to_the_internet()
    {
        var conf = Render([Domain() with { Access = AccessLevel.Network }])[SiteFile(Domain().Id)];

        Assert.Contains("    <Location \"/\">\n        AuthMerging And\n"
            + "        Require ip 127.0.0.0/8 ::1 10.0.0.0/8 172.16.0.0/12 192.168.0.0/16 fc00::/7 fe80::/10\n    </Location>\n", conf);
    }

    [Fact]
    public void Htaccess_cannot_widen_htdocs_folders()
    {
        HtdocsEntry[] htdocs =
        [
            new("blog", @"D:\Anvil\htdocs\blog", false, null, AccessLevel.Network),
            new("pma", @"D:\Tools\phpMyAdmin", true, null, AccessLevel.ThisPc),
        ];

        var files = Render([], htdocs: htdocs);

        // The root guard skips folders with rules of their own, case-insensitively as Windows maps URLs to folders.
        Assert.Contains("    <LocationMatch \"(?i)^/(?!(?:pma)(?:/|$))\">\n        AuthMerging And\n        Require local\n    </LocationMatch>\n",
            files[@"vhosts\01-localhost.conf"]);
        var catchall = files[@"vhosts\00-catchall.conf"];
        Assert.Contains("    <LocationMatch \"(?i)^/(?!(?:pma)(?:/|$))\">\n        AuthMerging And\n"
            + "        Require ip 127.0.0.0/8 ::1 10.0.0.0/8 172.16.0.0/12 192.168.0.0/16 fc00::/7 fe80::/10\n    </LocationMatch>\n", catchall);
        Assert.Contains("    <LocationMatch \"(?i)^/pma(?:/|$)\">\n        AuthMerging And\n        Require local\n    </LocationMatch>\n", catchall);
    }

    [Fact]
    public void Htdocs_alias_entries_render_in_localhost_and_catchall()
    {
        HtdocsEntry[] htdocs =
        [
            new("blog", @"D:\Anvil\htdocs\blog", false, null, AccessLevel.Network),
            new("pma", @"D:\Tools\phpMyAdmin", true, null, AccessLevel.ThisPc),
        ];

        var files = Render([], htdocs: htdocs);

        Snapshot.Match(files[@"vhosts\01-localhost.conf"] + "=== catch-all ===\n" + files[@"vhosts\00-catchall.conf"], "htdocs-entries");
    }

    [Fact]
    public void Htdocs_entry_with_own_php_and_access_gets_directory_block()
    {
        HtdocsEntry[] htdocs = [new("legacy", @"D:\Anvil\htdocs\legacy", false, "8.4.27", AccessLevel.ThisPc)];
        SiteDefinition[] saved =
        [
            new()
            {
                Id = "htdocs-legacy", Kind = SiteKind.Htdocs, Type = SiteType.Php, Name = "legacy", Root = @"D:\Anvil\htdocs\legacy",
                PhpVersion = "8.4.27", Access = AccessLevel.ThisPc,
            },
        ];

        var files = Render(saved, htdocs: htdocs);

        var catchall = files[@"vhosts\00-catchall.conf"];
        Assert.Contains("    <Directory \"D:/Anvil/htdocs/legacy\">\n", catchall);
        Assert.Matches("(?s)<Directory \"D:/Anvil/htdocs/legacy\">.*?Require local\n.*?</Directory>", catchall);
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
        var shop = Domain() with { Cert = CertSource.LocalDev, Aliases = ["www.shop.example.com"] };
        SiteDefinition[] sites =
        [
            shop,
            Domain("app.test") with { Type = SiteType.Proxy, Root = "http://127.0.0.1:5173" },
            Domain("static.test") with { Type = SiteType.Static, Access = AccessLevel.ThisPc },
        ];

        var files = Render(sites, certs: new Dictionary<string, SiteCertificate> { [shop.Id] = Cert(shop.Id) });

        Assert.All(files.Values, text => Assert.DoesNotMatch(@"<VirtualHost\s+(\d|\[)", text));
        Assert.All(files.Values, text => Assert.DoesNotMatch(@"(?m)^\s*Server(Name|Alias) .*\[", text));
        Assert.All(files.Values.Where(t => t.Contains("<VirtualHost", StringComparison.Ordinal)),
            text => Assert.All(Regex.Matches(text, "<VirtualHost [^>]*>"), m => Assert.Matches(@"^<VirtualHost \*:\d+>$", m.Value)));
    }
}
