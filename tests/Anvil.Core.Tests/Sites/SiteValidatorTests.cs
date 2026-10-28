using Anvil.Core.Paths;
using Anvil.Core.Sites;

namespace Anvil.Core.Tests.Sites;

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

    private static SiteDefinition Htdocs(string folder) => new()
    {
        Id = SiteSet.IdFor(SiteKind.Htdocs, folder),
        Kind = SiteKind.Htdocs,
        Type = SiteType.Php,
        Name = folder,
        Root = $@"D:\Anvil\htdocs\{folder}",
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
    public void Rejects_an_unknown_access_or_certificate_value() =>
        Assert.Equal(["Access", "Cert"], Fields(Domain("shop.example.com") with { Access = (AccessLevel)7, Cert = (CertSource)7 }));

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
    public void Accepts_a_valid_domain_site()
    {
        var site = Domain("shop.example.com", "www.shop.example.com") with { Cert = CertSource.LocalDev, Access = AccessLevel.ThisPc };

        Assert.Empty(SiteValidator.Validate(site, [Domain("blog.test"), Htdocs("blog")]));
        Assert.Equal("shop.example.com", site.Id);
        Assert.Equal("htdocs-blog", SiteSet.IdFor(SiteKind.Htdocs, "Blog"));
    }

    [Fact]
    public void Editing_a_site_does_not_clash_with_itself()
    {
        var shop = Domain("shop.example.com", "www.shop.example.com");

        Assert.Empty(Fields(shop with { Root = @"D:\Sites\shop2" }, shop));
    }

    private static readonly AppPaths App = new(@"D:\Anvil");

    // data\ holds the settings, keys and logs: a site must not publish it, nor Anvil's binaries.
    [Theory]
    [InlineData(@"D:\Anvil")]
    [InlineData(@"D:\anvil\")]
    [InlineData(@"D:\")]
    [InlineData(@"D:\Anvil\data\sites")]
    [InlineData(@"D:\Anvil\runtime\php")]
    [InlineData(@"D:/Anvil/catalog")]
    public void Rejects_a_root_that_would_publish_anvil_files(string root)
    {
        var errors = SiteValidator.Validate(Domain("shop.example.com") with { Root = root }, [], App);

        var error = Assert.Single(errors);
        Assert.Equal("Root", error.Field);
        Assert.Equal("This folder holds Anvil's own files, so the site would publish its settings and keys. Choose the site's own folder.",
            error.Message);
    }

    [Theory]
    [InlineData(@"D:\Anvil\htdocs\shop")]
    [InlineData(@"D:\Anvil\projects\shop")]
    [InlineData(@"D:\AnvilSites\shop")]
    public void Accepts_a_root_beside_anvil_files(string root) =>
        Assert.Empty(SiteValidator.Validate(Domain("shop.example.com") with { Root = root }, [], App));
}
