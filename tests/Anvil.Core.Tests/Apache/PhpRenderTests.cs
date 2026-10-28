using System.Text.RegularExpressions;
using Anvil.Core.Apache;
using Anvil.Core.Paths;
using Anvil.Core.Php;
using Anvil.Core.Settings;

namespace Anvil.Core.Tests.Apache;

public sealed class PhpRenderTests
{
    private static readonly AppPaths Paths = new(@"D:\Anvil");
    private static readonly StockConfState Stock = new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<int>([80]));

    private static PhpPool Pool(string version, int port) =>
        new(version, PhpVersionKey.Of(version), port, 4, 1000, $@"D:\Anvil\runtime\php\{version}\php-cgi.exe",
            $@"D:\Anvil\data\php\{version}", $@"D:\Anvil\data\php\{version}\conf.d");

    private static IReadOnlyDictionary<string, string> Render(string? defaultPhp = "8.5.12", params PhpPool[] pools) =>
        new ConfigRenderer(Paths).Render(new RenderInput
        {
            Settings = new AppSettings { DefaultPhp = defaultPhp },
            Stock = Stock,
            Pools = pools,
        }).Files;

    private static int Count(string text, string needle) => Regex.Matches(text, Regex.Escape(needle)).Count;

    [Fact]
    public void Pool_proxy_points_at_pool_port()
    {
        var pools = Render(pools: Pool("8.5.12", 9100))["php-pools.conf"];

        Assert.Contains("<Proxy \"fcgi://127.0.0.1:9100/\">\n    ProxySet timeout=300 retry=0\n</Proxy>", pools.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void Handler_url_ends_with_slash()
    {
        var localhost = Render(pools: Pool("8.5.12", 9100))[@"vhosts\01-localhost.conf"].ReplaceLineEndings("\n");

        Assert.Contains(
            "    <FilesMatch \"\\.php$\">\n        <If \"-f %{REQUEST_FILENAME}\">\n            SetHandler \"proxy:fcgi://127.0.0.1:9100/\"\n        </If>\n    </FilesMatch>\n",
            localhost, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_filename_fix_rendered_once()
    {
        var pools = Render(pools: [Pool("8.5.12", 9100), Pool("8.4.27", 9101)])["php-pools.conf"];

        Assert.Equal(1, Count(pools, "ProxyFCGIBackendType GENERIC"));
        Assert.Equal(1, Count(pools, "ProxyFCGISetEnvIf \"reqenv('SCRIPT_FILENAME') =~ m#^/([A-Za-z]:/.*)$#\" SCRIPT_FILENAME \"$1\""));
        Assert.Equal(2, Count(pools, "<Proxy "));
    }

    [Fact]
    public void Php_pools_included_before_vhosts()
    {
        var conf = Render(pools: Pool("8.5.12", 9100))["anvil.conf"];

        var include = conf.IndexOf("Include \"D:/Anvil/data/conf/php-pools.conf\"", StringComparison.Ordinal);
        Assert.True(include > 0);
        Assert.True(include < conf.IndexOf("IncludeOptional \"D:/Anvil/data/conf/vhosts/*.conf\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Backend_timeout_is_the_development_value()
    {
        var pools = Render(pools: Pool("8.5.12", 9100))["php-pools.conf"];

        Assert.Contains("ProxySet timeout=300 retry=0", pools, StringComparison.Ordinal);
    }

    [Fact]
    public void Htdocs_uses_default_php_pool()
    {
        var files = Render(defaultPhp: "8.4.27", pools: [Pool("8.5.12", 9100), Pool("8.4.27", 9101)]);

        foreach (var vhost in new[] { @"vhosts\00-catchall.conf", @"vhosts\01-localhost.conf" })
        {
            Assert.Contains("SetHandler \"proxy:fcgi://127.0.0.1:9101/\"", files[vhost], StringComparison.Ordinal);
            Assert.DoesNotContain("9100", files[vhost], StringComparison.Ordinal);
            Assert.Contains("DirectoryIndex index.php index.html", files[vhost], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void No_pools_renders_no_php_handler()
    {
        var files = Render(pools: []);

        Assert.All(files.Values, text =>
        {
            Assert.DoesNotContain("SetHandler", text, StringComparison.Ordinal);
            Assert.DoesNotContain("<Proxy", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ProxyFCGI", text, StringComparison.Ordinal);
            Assert.DoesNotContain("index.php", text, StringComparison.Ordinal);
        });
    }
}
