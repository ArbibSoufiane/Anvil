using System.Text;
using ArbiPache.Core.Apache;
using ArbiPache.Core.Modes;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Settings;
using ArbiPache.Core.Tests.Support;

namespace ArbiPache.Core.Tests.Apache;

public sealed class ConfigRendererTests
{
    private static readonly AppPaths Paths = new(@"D:\ArbiPache");

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
    public void Renders_expected_files()
    {
        var files = new ConfigRenderer(Paths).Render(Input()).Files.Keys.Order(StringComparer.Ordinal);

        Assert.Equal(["arbipache.conf", "mode.conf", @"vhosts\00-catchall.conf", @"vhosts\01-localhost.conf"], files);
    }

    [Fact]
    public void Skips_modules_already_loaded_by_stock_conf()
    {
        var conf = new ConfigRenderer(Paths).Render(Input()).Files["arbipache.conf"];

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
        var conf = new ConfigRenderer(Paths).Render(Input()).Files["arbipache.conf"];

        Assert.DoesNotContain("Listen 80\n", conf.ReplaceLineEndings("\n"));
        Assert.Contains("Listen 443\n", conf.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Custom_ports_are_listened()
    {
        var input = Input() with { Settings = Settings() with { HttpPort = 8080, HttpsPort = 8443 } };

        var files = new ConfigRenderer(Paths).Render(input).Files;

        Assert.Contains("Listen 8080\n", files["arbipache.conf"].ReplaceLineEndings("\n"));
        Assert.Contains("Listen 8443\n", files["arbipache.conf"].ReplaceLineEndings("\n"));
        Assert.Contains("<VirtualHost *:8080>", files[@"vhosts\00-catchall.conf"]);
    }

    [Fact]
    public void Paths_with_spaces_and_unicode_render_quoted()
    {
        var files = new ConfigRenderer(new AppPaths(@"D:\Mes Sites\café\ArbiPache")).Render(Input()).Files;

        Assert.Contains("PidFile \"D:/Mes Sites/café/ArbiPache/data/run/httpd.pid\"", files["arbipache.conf"]);
        Assert.Contains("DocumentRoot \"D:/Mes Sites/café/ArbiPache/htdocs\"", files[@"vhosts\01-localhost.conf"]);
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
