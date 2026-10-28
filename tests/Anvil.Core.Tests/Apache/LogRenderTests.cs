using Anvil.Core.Apache;
using Anvil.Core.Certificates;
using Anvil.Core.Paths;
using Anvil.Core.Settings;
using Anvil.Core.Sites;

namespace Anvil.Core.Tests.Apache;

/// <summary>Apache writes every log through rotatelogs: one file per day, so the Janitor can delete old days.</summary>
public sealed class LogRenderTests
{
    private static readonly AppPaths Paths = new(@"D:\Anvil");
    private static readonly StockConfState Stock = new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<int>([80]));

    private static SiteDefinition Shop(CertSource cert = CertSource.None) => new()
    {
        Id = "shop.example.com",
        Kind = SiteKind.Domain,
        Type = SiteType.Static,
        Name = "shop.example.com",
        Root = @"D:\Sites\shop",
        Cert = cert,
    };

    private static IReadOnlyDictionary<string, string> Render(SiteDefinition site, bool https = false) =>
        new ConfigRenderer(Paths).Render(new RenderInput
        {
            Settings = new AppSettings(),
            Sites = [site],
            Stock = Stock,
            Certificates = https
                ? new Dictionary<string, SiteCertificate>
                {
                    [site.Id] = new(CertSource.Imported, [site.Id], DateTimeOffset.UtcNow.AddDays(90), @"D:\Anvil\data\certs\shop.example.com\cert.pem",
                        @"D:\Anvil\data\certs\shop.example.com\key.pem", null),
                }
                : new Dictionary<string, SiteCertificate>(),
        }).Files.ToDictionary(f => f.Key, f => f.Value.ReplaceLineEndings("\n"));

    private static string Piped(string file) =>
        $"\"|\\\"${{SRVROOT}}/bin/rotatelogs.exe\\\" -l \\\"D:/Anvil/data/logs/{file}.%Y-%m-%d.log\\\" 86400\"";

    [Fact]
    public void Site_logs_use_rotatelogs_quoted()
    {
        var conf = Render(Shop())[@"vhosts\site-shop.example.com.conf"];

        Assert.Contains($"    ErrorLog {Piped("sites/shop.example.com/error")}\n", conf);
        Assert.Contains($"    CustomLog {Piped("sites/shop.example.com/access")} combined\n", conf);
    }

    [Fact]
    public void An_https_site_shares_its_error_log_and_keeps_a_second_access_log()
    {
        var conf = Render(Shop(CertSource.Imported), https: true)[@"vhosts\site-shop.example.com.conf"];

        // Apache opens one pipe for identical ErrorLog lines, but one per CustomLog line: two writers must not share a file.
        Assert.Equal(2, conf.Split($"    ErrorLog {Piped("sites/shop.example.com/error")}\n").Length - 1);
        Assert.Contains($"    CustomLog {Piped("sites/shop.example.com/access")} combined\n", conf);
        Assert.Contains($"    CustomLog {Piped("sites/shop.example.com/ssl-access")} combined\n", conf);
    }

    [Fact]
    public void Apache_main_logs_rotate()
    {
        var conf = Render(Shop())["anvil.conf"];

        Assert.Contains($"ErrorLog {Piped("apache/error")}\n", conf);
        Assert.Contains($"    CustomLog {Piped("apache/access")} combined\n", conf);
    }

    [Fact]
    public void A_new_httpd_conf_leaves_the_access_log_to_Anvil()
    {
        var root = Directory.CreateTempSubdirectory("ap-httpd-").FullName;
        try
        {
            var paths = new AppPaths(root);
            var stockConf = Path.Combine(paths.ApacheRuntime("2.4.69"), "conf");
            Directory.CreateDirectory(stockConf);
            File.WriteAllText(Path.Combine(stockConf, "httpd.conf"),
                "<IfModule log_config_module>\r\n    LogFormat \"%h %l %u %t\" common\r\n    CustomLog \"logs/access.log\" common\r\n</IfModule>\r\n");

            new HttpdConfBootstrap(paths).EnsureUserConf("2.4.69");

            var conf = File.ReadAllText(paths.HttpdConf);
            Assert.Contains("    #CustomLog \"logs/access.log\" common  # Anvil writes the access log to data\\logs\\apache, one file per day\r\n", conf);
            Assert.DoesNotContain("\n    CustomLog", conf);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
