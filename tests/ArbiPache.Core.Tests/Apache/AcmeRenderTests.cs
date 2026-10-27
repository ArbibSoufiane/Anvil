using ArbiPache.Core.Apache;
using ArbiPache.Core.Certificates;
using ArbiPache.Core.Modes;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Settings;
using ArbiPache.Core.Sites;

namespace ArbiPache.Core.Tests.Apache;

public sealed class AcmeRenderTests
{
    private static readonly AppPaths Paths = new(@"D:\ArbiPache");

    private static IReadOnlyDictionary<string, string> Render(params SiteDefinition[] sites)
    {
        var certs = sites.Where(s => s.Cert == CertSource.LetsEncrypt).ToDictionary(s => s.Id, s =>
            new SiteCertificate(CertSource.LetsEncrypt, [s.Name], DateTimeOffset.UtcNow.AddDays(60), $@"D:\ArbiPache\data\certs\{s.Id}\cert.pem",
                $@"D:\ArbiPache\data\certs\{s.Id}\key.pem", $@"D:\ArbiPache\data\certs\{s.Id}\chain.pem"));
        return new ConfigRenderer(Paths).Render(new RenderInput
        {
            Settings = new AppSettings { Mode = ServerMode.Production },
            Stock = new StockConfState(new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<int>()),
            Profile = BuiltInProfiles.For(ServerMode.Production),
            Sites = sites,
            Certificates = certs,
        }).Files.ToDictionary(f => f.Key, f => f.Value.ReplaceLineEndings("\n"));
    }

    private static SiteDefinition Site(SiteType type = SiteType.Php, AccessLevel access = AccessLevel.Everyone) => new()
    {
        Id = "shop.example.com",
        Kind = SiteKind.Domain,
        Type = type,
        Name = "shop.example.com",
        Root = type == SiteType.Proxy ? "http://127.0.0.1:3000" : @"D:\Sites\shop\public",
        Cert = CertSource.LetsEncrypt,
        Access = access,
    };

    [Fact]
    public void Global_conf_has_acme_alias()
    {
        var conf = Render()["arbipache.conf"];

        Assert.Contains("LoadModule alias_module modules/mod_alias.so", conf);
        const string expected = """
            Alias "/.well-known/acme-challenge/" "D:/ArbiPache/data/acme/.well-known/acme-challenge/"
            <Directory "D:/ArbiPache/data/acme/.well-known/acme-challenge">
                Options None
                AllowOverride None
                Require all granted
            </Directory>
            """;
        Assert.Contains(expected.ReplaceLineEndings("\n"), conf);
        Assert.True(conf.IndexOf("Alias \"/.well-known/acme-challenge/\"", StringComparison.Ordinal) < conf.IndexOf("IncludeOptional", StringComparison.Ordinal));
    }

    [Fact]
    public void Https_redirect_skips_acme_path()
    {
        var conf = Render(Site())[@"vhosts\site-shop.example.com.conf"];

        Assert.Contains("RedirectMatch 301 ^/(?!\\.well-known/acme-challenge/)(.*)$ https://shop.example.com/$1", conf);
    }

    [Fact]
    public void Proxy_site_answers_acme_challenges()
    {
        var conf = Render(Site(SiteType.Proxy, AccessLevel.ThisPc) with { HttpRedirect = false })[@"vhosts\site-shop.example.com.conf"];

        var keep = conf.IndexOf("ProxyPass \"/.well-known/acme-challenge/\" !", StringComparison.Ordinal);
        Assert.True(keep >= 0 && keep < conf.IndexOf("ProxyPass / ", StringComparison.Ordinal), conf);
        const string grant = """
                <Location "/.well-known/acme-challenge/">
                    Require all granted
                </Location>
            """;
        var granted = conf.IndexOf(grant.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.True(granted > conf.IndexOf("<Location \"/\">", StringComparison.Ordinal), conf);
    }
}
