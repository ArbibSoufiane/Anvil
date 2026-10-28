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
        Mounts = [new SiteMount("/demo", @"D:\Sites\demo", SiteType.Static, null)],
        Cert = CertSource.LoadBalancer,
        TrustedProxies = ["10.0.0.5"],
        AcceptOnlyFromProxies = true,
        Access = AccessLevel.SpecificIps,
        AllowedIps = ["203.0.113.0/24"],
        PasswordProtected = true,
        HttpRedirect = false,
        AddToHosts = true,
        Enabled = false,
        Created = new DateTimeOffset(2026, 10, 27, 9, 30, 0, TimeSpan.Zero),
    };

    private static void AssertSame(SiteDefinition expected, SiteDefinition actual)
    {
        Assert.Equal(expected with { Aliases = [], Index = [], Mounts = [], TrustedProxies = [], AllowedIps = [] },
            actual with { Aliases = [], Index = [], Mounts = [], TrustedProxies = [], AllowedIps = [] });
        Assert.Equal(expected.Aliases, actual.Aliases);
        Assert.Equal(expected.Index, actual.Index);
        Assert.Equal(expected.Mounts, actual.Mounts);
        Assert.Equal(expected.TrustedProxies, actual.TrustedProxies);
        Assert.Equal(expected.AllowedIps, actual.AllowedIps);
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

    [Theory]
    [InlineData(@"..\escape")]
    [InlineData("a/b")]
    [InlineData("")]
    public void Unsafe_ids_are_refused(string id) =>
        Assert.Throws<ArgumentException>(() => new SiteStore(_paths).Save(Shop() with { Id = id }));

    [Fact]
    public void RequiredPhpVersions_includes_mount_versions_and_default()
    {
        SiteDefinition[] sites =
        [
            Shop() with { Enabled = true, PhpVersion = null, Mounts = [new SiteMount("/demo", @"D:\Sites\demo", SiteType.Php, "8.4.27")] },
            Shop() with { Id = "legacy.test", Name = "legacy.test", Enabled = true, PhpVersion = "7.4.33", Mounts = [] },
            Shop() with { Id = "off.test", Name = "off.test", Enabled = false, PhpVersion = "8.3.27", Mounts = [] },
            Shop() with { Id = "vite.test", Name = "vite.test", Enabled = true, Type = SiteType.Proxy, PhpVersion = "8.2.29", Mounts = [] },
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
}
