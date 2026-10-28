using ArbiPache.Core.Paths;
using ArbiPache.Core.Sites;

namespace ArbiPache.Core.Tests.Sites;

public sealed class SiteValidatorTests
{
    private static SiteDefinition Domain(string name, params string[] aliases) => new()
    {
        Id = SiteSet.IdFor(SiteKind.Domain, name),
        Kind = SiteKind.Domain,
        Type = SiteType.Php,
        Name = name,
        Root = @"D:\Sites\shop\public",
        Aliases = aliases,
    };

    private static SiteDefinition Ip(string address) => new()
    {
        Id = SiteSet.IdFor(SiteKind.Ip, address),
        Kind = SiteKind.Ip,
        Type = SiteType.Php,
        Name = address,
        Root = @"D:\Sites\shop\public",
    };

    private static SiteDefinition Htdocs(string folder) => new()
    {
        Id = SiteSet.IdFor(SiteKind.Htdocs, folder),
        Kind = SiteKind.Htdocs,
        Type = SiteType.Php,
        Name = folder,
        Root = $@"D:\ArbiPache\htdocs\{folder}",
        Access = AccessLevel.Network,
    };

    private static IReadOnlyList<string> Fields(SiteDefinition s, params SiteDefinition[] others) =>
        SiteValidator.Validate(s, others).Select(e => e.Field).ToList();

    [Theory]
    [InlineData("shop")]
    [InlineData("-shop.example.com")]
    [InlineData("shop..example.com")]
    [InlineData("shop.example.c")]
    [InlineData("shop_1.example.com")]
    public void Rejects_invalid_domain(string name) => Assert.Equal(["Name"], Fields(Domain(name)));

    [Fact]
    public void Rejects_localhost() => Assert.Equal(["Name"], Fields(Domain("localhost")));

    [Fact]
    public void Accepts_idn_domain_as_punycode()
    {
        Assert.Empty(Fields(Domain("Bücher.Example.com")));
        Assert.Equal("xn--bcher-kva.example.com", SiteSet.IdFor(SiteKind.Domain, "Bücher.Example.com"));
    }

    [Fact]
    public void Rejects_duplicate_alias()
    {
        var shop = Domain("shop.example.com", "www.shop.example.com");

        Assert.Equal(["Aliases"], Fields(Domain("portal.example.com", "WWW.shop.example.com"), shop));
        Assert.Equal(["Name"], Fields(Domain("www.shop.example.com"), shop));
        Assert.Equal(["Aliases"], Fields(Domain("blog.example.com", "a.example.com", "a.example.com")));
    }

    [Fact]
    public void Rejects_second_ip_site_same_ip()
    {
        Assert.Equal(["Name"], Fields(Ip("192.168.10.2"), Ip("192.168.10.2") with { Id = "other" }));
        Assert.Empty(Fields(Ip("192.168.10.3"), Ip("192.168.10.2")));
    }

    [Theory]
    [InlineData("192.168.10.300")]
    [InlineData("shop.example.com")]
    public void Rejects_invalid_ip(string address) => Assert.Equal(["Name"], Fields(Ip(address)));

    [Theory]
    [InlineData("demo")]
    [InlineData("/")]
    [InlineData("/Demo")]
    [InlineData("/demo/")]
    [InlineData("/de mo")]
    public void Rejects_bad_mount_path(string path) =>
        Assert.Equal(["Mounts"], Fields(Ip("*") with { Mounts = [new SiteMount(path, @"D:\Sites\demo", SiteType.Php, null)] }));

    [Fact]
    public void Rejects_duplicate_mount_path() =>
        Assert.Equal(["Mounts"], Fields(Ip("*") with
        {
            Mounts = [new SiteMount("/demo", @"D:\Sites\a", SiteType.Php, null), new SiteMount("/demo", @"D:\Sites\b", SiteType.Static, null)],
        }));

    [Fact]
    public void Mounts_only_on_ip_and_domain_sites() =>
        Assert.Equal(["Mounts"], Fields(Htdocs("blog") with { Mounts = [new SiteMount("/demo", @"D:\Sites\demo", SiteType.Php, null)] }));

    [Fact]
    public void SpecificIps_requires_ip()
    {
        var site = Domain("shop.example.com") with { Access = AccessLevel.SpecificIps };

        Assert.Equal(["AllowedIps"], Fields(site));
        Assert.Equal(["AllowedIps"], Fields(site with { AllowedIps = ["10.0.0.1", "not-an-ip"] }));
        Assert.Empty(Fields(site with { AllowedIps = ["10.0.0.1", "192.168.0.0/16", "2001:db8::/32"] }));
    }

    [Fact]
    public void LoadBalancer_requires_proxy()
    {
        var site = Domain("shop.example.com") with { Cert = CertSource.LoadBalancer };

        Assert.Equal(["TrustedProxies"], Fields(site));
        Assert.Equal(["TrustedProxies"], Fields(site with { TrustedProxies = ["lb.example.com"] }));
        Assert.Empty(Fields(site with { TrustedProxies = ["10.0.0.5", "10.0.1.0/24"] }));
    }

    [Fact]
    public void LetsEncrypt_rejected_for_ip_site() => Assert.Equal(["Cert"], Fields(Ip("192.168.10.2") with { Cert = CertSource.LetsEncrypt }));

    [Theory]
    [InlineData("shop.test")]
    [InlineData("shop.local")]
    [InlineData("shop.localhost")]
    public void LetsEncrypt_rejected_for_test_tld(string name) =>
        Assert.Equal(["Cert"], Fields(Domain(name) with { Cert = CertSource.LetsEncrypt }));

    [Fact]
    public void Htdocs_cannot_be_everyone() => Assert.Equal(["Access"], Fields(Htdocs("blog") with { Access = AccessLevel.Everyone }));

    [Theory]
    [InlineData(".hidden")]
    [InlineData("my blog")]
    [InlineData("blog/x")]
    public void Rejects_bad_htdocs_folder_name(string folder) => Assert.Equal(["Name"], Fields(Htdocs(folder)));

    [Theory]
    [InlineData("localhost:5173")]
    [InlineData(@"D:\Sites\vite")]
    [InlineData("ftp://localhost:5173")]
    public void Proxy_requires_http_url(string root) =>
        Assert.Equal(["Root"], Fields(Domain("app.test") with { Type = SiteType.Proxy, Root = root }));

    [Fact]
    public void Accepts_valid_ip_site_with_demo_mount()
    {
        var site = Ip("192.168.10.2") with
        {
            Cert = CertSource.LoadBalancer,
            TrustedProxies = ["10.0.0.5"],
            Mounts = [new SiteMount("/demo", @"D:\Sites\shop-demo\public", SiteType.Php, "8.4.27"), new SiteMount("/staging/v2", @"D:\Sites\v2", SiteType.Static, null)],
        };

        Assert.Empty(SiteValidator.Validate(site, [Domain("shop.example.com"), Ip("*")]));
        Assert.Equal("ip-192-168-10-2", site.Id);
        Assert.Equal("ip-all", SiteSet.IdFor(SiteKind.Ip, "*"));
        Assert.Equal("htdocs-blog", SiteSet.IdFor(SiteKind.Htdocs, "Blog"));
    }

    [Fact]
    public void Editing_a_site_does_not_clash_with_itself()
    {
        var shop = Domain("shop.example.com", "www.shop.example.com");

        Assert.Empty(Fields(shop with { Root = @"D:\Sites\shop2" }, shop));
    }

    private static readonly AppPaths App = new(@"D:\ArbiPache");

    // data\ holds the settings, keys and logs: a site must not publish it, nor ArbiPache's binaries.
    [Theory]
    [InlineData(@"D:\ArbiPache")]
    [InlineData(@"D:\arbipache\")]
    [InlineData(@"D:\")]
    [InlineData(@"D:\ArbiPache\data\sites")]
    [InlineData(@"D:\ArbiPache\runtime\php")]
    [InlineData(@"D:/ArbiPache/catalog")]
    public void Rejects_a_root_that_would_publish_arbipache_files(string root)
    {
        var errors = SiteValidator.Validate(Domain("shop.example.com") with { Root = root }, [], App);

        var error = Assert.Single(errors);
        Assert.Equal("Root", error.Field);
        Assert.Equal("This folder holds ArbiPache's own files, so the site would publish its settings and keys. Choose the site's own folder.",
            error.Message);
    }

    [Theory]
    [InlineData(@"D:\ArbiPache\htdocs\shop")]
    [InlineData(@"D:\ArbiPache\projects\shop")]
    [InlineData(@"D:\ArbiPacheSites\shop")]
    public void Accepts_a_root_beside_arbipache_files(string root) =>
        Assert.Empty(SiteValidator.Validate(Domain("shop.example.com") with { Root = root }, [], App));

    [Fact]
    public void Rejects_a_mount_that_would_publish_arbipache_files()
    {
        var site = Domain("shop.example.com") with { Mounts = [new SiteMount("/demo", @"D:\ArbiPache\data", SiteType.Static, null)] };

        Assert.Equal(["Mounts"], SiteValidator.Validate(site, [], App).Select(e => e.Field));
    }
}
