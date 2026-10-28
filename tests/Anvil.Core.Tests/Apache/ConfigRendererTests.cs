using System.Text;
using Anvil.Core.Apache;
using Anvil.Core.Certificates;
using Anvil.Core.Modes;
using Anvil.Core.Paths;
using Anvil.Core.Php;
using Anvil.Core.Settings;
using Anvil.Core.Sites;
using Anvil.Core.Tests.Support;

namespace Anvil.Core.Tests.Apache;

public sealed class ConfigRendererTests
{
    private static readonly AppPaths Paths = new(@"D:\Anvil");

    // What Apache Lounge 2.4.69's stock httpd.conf loads and listens on.
    private static readonly StockConfState Stock = new(
        new HashSet<string>(["access_compat", "auth_basic", "authn_core", "authn_file", "authz_core", "authz_groupfile",
            "authz_host", "authz_user", "filter", "reqtimeout", "autoindex", "mime", "log_config", "alias", "dir", "env",
            "headers", "setenvif", "version"], StringComparer.OrdinalIgnoreCase),
        new HashSet<int>([80]));

    private static AppSettings Settings(ServerMode mode = ServerMode.Development) =>
        new() { Mode = mode, Apache = new ApacheTuning(256, 0, 5) };

    private static RenderInput Input(ServerMode mode = ServerMode.Development) => new()
    {
        Settings = Settings(mode),
        Stock = Stock,
        Profile = BuiltInProfiles.For(mode),
        ApacheVersion = "2.4.69",
    };

    private static RenderInput WithHttpsSite(RenderInput input, string name = "shop.example.com") => input with
    {
        Sites = [new SiteDefinition { Id = "shop", Kind = SiteKind.Domain, Type = SiteType.Static, Name = name, Root = @"D:\www\shop", Cert = CertSource.LocalDev }],
        Certificates = new Dictionary<string, SiteCertificate>
        {
            ["shop"] = new(CertSource.LocalDev, [name], DateTimeOffset.UtcNow.AddDays(60), @"D:\Anvil\data\certs\shop\cert.pem",
                @"D:\Anvil\data\certs\shop\key.pem", null),
        },
    };

    private static string Flatten(RenderedConfig config)
    {
        var sb = new StringBuilder();
        foreach (var (path, text) in config.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
            sb.Append("=== ").Append(path).Append(" ===\n").Append(text.ReplaceLineEndings("\n")).Append('\n');
        return sb.ToString();
    }

    [Fact]
    public void Renders_development_snapshot() =>
        Snapshot.Match(Flatten(new ConfigRenderer(Paths).Render(Input())), "development");

    [Fact]
    public void Renders_production_snapshot() =>
        Snapshot.Match(Flatten(new ConfigRenderer(Paths).Render(Input(ServerMode.Production))), "production");

    [Fact]
    public void Renders_php_snapshot() =>
        Snapshot.Match(Flatten(new ConfigRenderer(Paths).Render(Input() with
        {
            Settings = Settings() with { DefaultPhp = "8.5.12" },
            Pools =
            [
                new PhpPool("8.5.12", "php85", 9100, 4, 1000, @"D:\Anvil\runtime\php\8.5.12\php-cgi.exe", @"D:\Anvil\data\php\8.5.12", @"D:\Anvil\data\php\8.5.12\conf.d"),
                new PhpPool("8.4.27", "php84", 9101, 2, 500, @"D:\Anvil\runtime\php\8.4.27\php-cgi.exe", @"D:\Anvil\data\php\8.4.27", @"D:\Anvil\data\php\8.4.27\conf.d"),
            ],
        })), "php");

    [Fact]
    public void Renders_expected_files()
    {
        var files = new ConfigRenderer(Paths).Render(Input()).Files.Keys.Order(StringComparer.Ordinal);

        Assert.Equal(["anvil.conf", "mode.conf", "php-pools.conf", @"vhosts\00-catchall.conf", @"vhosts\01-localhost.conf"], files);
    }

    // A site or htdocs folder set wider than its own folder must still never serve data\: settings, keys and logs.
    [Fact]
    public void Data_folder_is_never_served()
    {
        var conf = new ConfigRenderer(Paths).Render(Input()).Files["anvil.conf"].ReplaceLineEndings("\n");

        Assert.Contains("<Directory \"D:/Anvil/data\">\n    Options None\n    AllowOverride None\n    Require all denied\n</Directory>\n", conf);
    }

    [Fact]
    public void Skips_modules_already_loaded_by_stock_conf()
    {
        var conf = new ConfigRenderer(Paths).Render(Input()).Files["anvil.conf"];

        Assert.Contains("LoadModule ssl_module modules/mod_ssl.so", conf);
        Assert.Contains("LoadModule proxy_fcgi_module modules/mod_proxy_fcgi.so", conf);
        Assert.Contains("LoadModule lbmethod_bybusyness_module modules/mod_lbmethod_bybusyness.so", conf);
        Assert.Contains("LoadModule http2_module modules/mod_http2.so", conf);
        Assert.DoesNotContain("LoadModule headers_module", conf);
        Assert.DoesNotContain("LoadModule auth_basic_module", conf);
        Assert.DoesNotContain("LoadModule reqtimeout_module", conf);
    }

    [Fact]
    public void Skips_listen_already_in_stock_conf()
    {
        var conf = new ConfigRenderer(Paths).Render(WithHttpsSite(Input())).Files["anvil.conf"];

        Assert.DoesNotContain("Listen 80\n", conf.ReplaceLineEndings("\n"));
        Assert.Contains("Listen 443\n", conf.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Custom_ports_are_listened()
    {
        var input = WithHttpsSite(Input()) with { Settings = Settings() with { HttpPort = 8080, HttpsPort = 8443 } };

        var files = new ConfigRenderer(Paths).Render(input).Files;

        Assert.Contains("Listen 8080\n", files["anvil.conf"].ReplaceLineEndings("\n"));
        Assert.Contains("Listen 8443\n", files["anvil.conf"].ReplaceLineEndings("\n"));
        Assert.Contains("<VirtualHost *:8080>", files[@"vhosts\00-catchall.conf"]);
    }

    // The first HTTPS virtual host answers every name no site claims; without one, that was whichever site sorted first.
    [Fact]
    public void Https_refuses_names_no_site_claims()
    {
        var files = new ConfigRenderer(Paths).Render(WithHttpsSite(Input(ServerMode.Production))).Files;

        var conf = files[@"vhosts\00-catchall-https.conf"].ReplaceLineEndings("\n");
        Assert.Contains("<VirtualHost *:443>", conf);
        Assert.Contains("    SSLCertificateFile \"D:/Anvil/data/certs/shop/cert.pem\"\n", conf);
        Assert.Contains("    <Location \"/\">\n        Require all denied\n    </Location>\n", conf);
        Assert.Contains("Listen 443\n", files["anvil.conf"].ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Without_https_sites_nothing_listens_on_the_https_port()
    {
        var files = new ConfigRenderer(Paths).Render(Input(ServerMode.Production)).Files;

        Assert.DoesNotContain("Listen 443", files["anvil.conf"]);
        Assert.DoesNotContain(@"vhosts\00-catchall-https.conf", files.Keys);
    }

    [Fact]
    public void Paths_with_spaces_and_unicode_render_quoted()
    {
        var files = new ConfigRenderer(new AppPaths(@"D:\Mes Sites\café\Anvil")).Render(Input()).Files;

        Assert.Contains("PidFile \"D:/Mes Sites/café/Anvil/data/run/httpd.pid\"", files["anvil.conf"]);
        Assert.Contains("DocumentRoot \"D:/Mes Sites/café/Anvil/htdocs\"", files[@"vhosts\01-localhost.conf"]);
    }

    [Fact]
    public void Custom_htdocs_path_is_document_root()
    {
        var input = Input() with { Settings = Settings() with { HtdocsPath = @"E:\www" } };

        var conf = new ConfigRenderer(Paths).Render(input).Files[@"vhosts\01-localhost.conf"];

        Assert.Contains("DocumentRoot \"E:/www\"", conf);
    }

    [Fact]
    public void Production_catchall_denies_all()
    {
        var conf = new ConfigRenderer(Paths).Render(Input(ServerMode.Production)).Files[@"vhosts\00-catchall.conf"];

        Assert.Contains("Require all denied", conf);
        Assert.Contains("ErrorDocument 403 \" \"", conf);
        Assert.DoesNotContain("Indexes", conf);
    }

    [Fact]
    public void Development_catchall_allows_private_networks_only()
    {
        var conf = new ConfigRenderer(Paths).Render(Input()).Files[@"vhosts\00-catchall.conf"];

        Assert.Contains("Require ip 127.0.0.0/8 ::1 10.0.0.0/8 172.16.0.0/12 192.168.0.0/16 fc00::/7 fe80::/10", conf);
        Assert.DoesNotContain("Require all granted", conf);
    }

    [Fact]
    public void Htdocs_off_network_limits_catchall_to_this_pc()
    {
        var input = Input() with { Settings = Settings() with { Network = new NetworkAccess(HtdocsOnNetwork: false, PrivateNetworksOnly: true) } };

        var conf = new ConfigRenderer(Paths).Render(input).Files[@"vhosts\00-catchall.conf"];

        Assert.Contains("Require local", conf);
        Assert.DoesNotContain("192.168.0.0/16", conf);
    }

    [Fact]
    public void StockConfScanner_ignores_commented_lines()
    {
        const string conf = "LoadModule ssl_module modules/mod_ssl.so\n#LoadModule rewrite_module modules/mod_rewrite.so\n  # LoadModule proxy_module x\nListen 80\n#Listen 8080\nListen 0.0.0.0:8081\nListen [::]:8082 http\n";

        var state = StockConfScanner.Scan(conf);

        Assert.Equal(["ssl"], state.LoadedModules);
        Assert.Equal([80, 8081, 8082], state.ListenPorts.Order());
    }
}
