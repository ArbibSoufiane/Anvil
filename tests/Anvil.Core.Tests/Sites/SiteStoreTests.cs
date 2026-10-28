using Anvil.Core.Paths;
using Anvil.Core.Sites;

namespace Anvil.Core.Tests.Sites;

public sealed class SiteStoreTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ap-sites-" + Guid.NewGuid().ToString("N")));

    public SiteStoreTests() => _paths.EnsureCreated();

    public void Dispose()
    {
        try { Directory.Delete(_paths.Root, recursive: true); } catch (IOException) { }
    }

    private static SiteDefinition Shop() => new()
    {
        Id = "shop.example.com",
        Kind = SiteKind.Domain,
        Type = SiteType.Php,
        Name = "shop.example.com",
        Root = @"D:\Sites\shop\public",
        PhpVersion = "8.5.12",
        Aliases = ["www.shop.example.com"],
        Cert = CertSource.LocalDev,
        Access = AccessLevel.ThisPc,
        HttpRedirect = false,
        AddToHosts = true,
        Enabled = false,
        Created = new DateTimeOffset(2026, 10, 27, 9, 30, 0, TimeSpan.Zero),
    };

    private static void AssertSame(SiteDefinition expected, SiteDefinition actual)
    {
        Assert.Equal(expected with { Aliases = [], Index = [] }, actual with { Aliases = [], Index = [] });
        Assert.Equal(expected.Aliases, actual.Aliases);
        Assert.Equal(expected.Index, actual.Index);
    }

    [Fact]
    public void Store_round_trip()
    {
        var store = new SiteStore(_paths);

        store.Save(Shop());

        Assert.True(File.Exists(Path.Combine(_paths.Sites, "shop.example.com.json")));
        AssertSame(Shop(), Assert.Single(new SiteStore(_paths).All()));
    }

    [Fact]
    public void Save_replaces_and_delete_removes()
    {
        var store = new SiteStore(_paths);
        store.Save(Shop());
        store.Save(Shop() with { Root = @"D:\Sites\shop2" });
        store.Save(Shop() with { Id = "blog.test", Name = "blog.test" });

        Assert.Equal(@"D:\Sites\shop2", store.All().Single(s => s.Id == "shop.example.com").Root);

        store.Delete("shop.example.com");

        Assert.Equal(["blog.test"], store.All().Select(s => s.Id));
    }

    [Fact]
    public void Unreadable_site_file_is_skipped_and_reported()
    {
        var store = new SiteStore(_paths);
        store.Save(Shop());
        File.WriteAllText(Path.Combine(_paths.Sites, "broken.test.json"), "{ not json");

        Assert.Equal(["shop.example.com"], store.All().Select(s => s.Id));
        Assert.StartsWith("broken.test.json could not be read", Assert.Single(store.Warnings));
        Assert.True(File.Exists(Path.Combine(_paths.Sites, "broken.test.json")));
    }

    [Fact]
    public void Ip_site_from_an_older_build_is_skipped_with_a_warning_and_its_file_kept()
    {
        var file = Path.Combine(_paths.Sites, "ip-192.168.1.5.json");
        var json = """{"id":"ip-192.168.1.5","kind":"Ip","type":"Php","name":"192.168.1.5","root":"D:\\Sites\\demo"}""";
        File.WriteAllText(file, json);
        var store = new SiteStore(_paths);

        Assert.Empty(store.All());
        Assert.Equal("ip-192.168.1.5.json: IP sites are no longer supported, so this site was skipped.", Assert.Single(store.Warnings));
        Assert.Equal(json, File.ReadAllText(file));
    }

    [Fact]
    public void Lets_encrypt_site_from_an_older_build_loads_as_http_only_open_to_the_network()
    {
        File.WriteAllText(Path.Combine(_paths.Sites, "shop.example.com.json"),
            """{"id":"shop.example.com","kind":"Domain","type":"Php","name":"shop.example.com","root":"D:\\Sites\\shop","cert":"LetsEncrypt","access":"Everyone","passwordProtected":true}""");

        var site = Assert.Single(new SiteStore(_paths).All());

        Assert.Equal(CertSource.None, site.Cert);
        Assert.Equal(AccessLevel.Network, site.Access);
    }

    [Theory]
    [InlineData(@"..\escape")]
    [InlineData("a/b")]
    [InlineData("")]
    public void Unsafe_ids_are_refused(string id) =>
        Assert.Throws<ArgumentException>(() => new SiteStore(_paths).Save(Shop() with { Id = id }));

    [Fact]
    public void RequiredPhpVersions_lists_enabled_php_sites_htdocs_and_default()
    {
        SiteDefinition[] sites =
        [
            Shop() with { Enabled = true, PhpVersion = null },
            Shop() with { Id = "demo.test", Name = "demo.test", Enabled = true, PhpVersion = "8.4.27" },
            Shop() with { Id = "legacy.test", Name = "legacy.test", Enabled = true, PhpVersion = "7.4.33" },
            Shop() with { Id = "off.test", Name = "off.test", Enabled = false, PhpVersion = "8.3.27" },
            Shop() with { Id = "vite.test", Name = "vite.test", Enabled = true, Type = SiteType.Proxy, PhpVersion = "8.2.29" },
        ];
        HtdocsEntry[] htdocs = [new("blog", @"D:\Anvil\htdocs\blog", false, "8.1.34", AccessLevel.Network)];

        var versions = SiteSet.RequiredPhpVersions(sites, htdocs, "8.5.12");

        Assert.Equal(["8.5.12", "8.4.27", "7.4.33", "8.1.34"], versions);
    }

    [Fact]
    public void DiscoverHtdocs_lists_folders_and_aliases()
    {
        Directory.CreateDirectory(Path.Combine(_paths.Htdocs, "blog"));
        Directory.CreateDirectory(Path.Combine(_paths.Htdocs, "phpinfo"));
        Directory.CreateDirectory(Path.Combine(_paths.Htdocs, ".git"));
        File.WriteAllText(Path.Combine(_paths.Htdocs, "index.html"), "hello");
        SiteDefinition[] overrides =
        [
            new()
            {
                Id = "htdocs-blog", Kind = SiteKind.Htdocs, Type = SiteType.Php, Name = "blog", Root = Path.Combine(_paths.Htdocs, "blog"),
                PhpVersion = "8.4.27", Access = AccessLevel.ThisPc,
            },
            new()
            {
                Id = "htdocs-tools", Kind = SiteKind.Htdocs, Type = SiteType.Php, Name = "tools", Root = @"D:\Tools\phpMyAdmin",
                Access = AccessLevel.ThisPc,
            },
            new()
            {
                Id = "htdocs-old", Kind = SiteKind.Htdocs, Type = SiteType.Php, Name = "old", Root = @"D:\Old", Access = AccessLevel.ThisPc, Enabled = false,
            },
        ];

        var entries = SiteSet.DiscoverHtdocs(_paths.Htdocs, overrides);

        Assert.Equal(
        [
            new HtdocsEntry("blog", Path.Combine(_paths.Htdocs, "blog"), false, "8.4.27", AccessLevel.ThisPc),
            new HtdocsEntry("phpinfo", Path.Combine(_paths.Htdocs, "phpinfo"), false, null, AccessLevel.Network),
            new HtdocsEntry("tools", @"D:\Tools\phpMyAdmin", true, null, AccessLevel.ThisPc),
        ], entries);
    }

    [Fact]
    public void Folders_htdocs_cannot_serve_are_listed()
    {
        foreach (var name in (string[])["blog", "old_site", "my app", "café", "a${b}", ".git"])
            Directory.CreateDirectory(Path.Combine(_paths.Htdocs, name));
        File.WriteAllText(Path.Combine(_paths.Htdocs, "read me.txt"), "x");

        Assert.Equal(["a${b}", "café", "my app"], SiteSet.UnservedFolders(_paths.Htdocs));
        Assert.Empty(SiteSet.UnservedFolders(Path.Combine(_paths.Htdocs, "missing")));
    }
}
