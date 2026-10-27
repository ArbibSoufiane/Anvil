using ArbiPache.Core.Elevation;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Sites;

namespace ArbiPache.Core.Tests.Elevation;

/// <summary>What the service account may read and change: only what httpd, PHP and the database need.</summary>
public sealed class AclPlannerTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "ap-acl-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public AclPlannerTests()
    {
        _paths = new AppPaths(Path.Combine(_temp, "ArbiPache"));
        _paths.EnsureCreated();
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { }
    }

    private string Folder(params string[] parts)
    {
        var folder = Path.Combine([_temp, .. parts]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private void Touch(params string[] parts)
    {
        var file = Path.Combine([_temp, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "");
    }

    private static SiteDefinition Site(string root, SiteType type = SiteType.Php, IReadOnlyList<SiteMount>? mounts = null) => new()
    {
        Id = "shop.test",
        Kind = SiteKind.Domain,
        Type = type,
        Name = "shop.test",
        Root = root,
        Mounts = mounts ?? [],
    };

    [Fact]
    public void Data_gets_modify()
    {
        var plan = AclPlanner.Plan(_paths, []);

        Assert.Contains(new AclGrant(_paths.Root, AclRight.ReadExecute), plan);
        Assert.Contains(new AclGrant(_paths.Data, AclRight.Modify), plan);
        Assert.Contains(new AclGrant(_paths.Htdocs, AclRight.Read), plan);
        Assert.Equal(3, plan.Count);
    }

    [Fact]
    public void Site_roots_get_read()
    {
        var shop = Folder("Sites", "shop");
        var demo = Folder("Sites", "demo");

        var plan = AclPlanner.Plan(_paths, [Site(shop, mounts: [new SiteMount("/demo", demo, SiteType.Static, null)])]);

        Assert.Contains(new AclGrant(shop, AclRight.Read), plan);
        Assert.Contains(new AclGrant(demo, AclRight.Read), plan);
    }

    [Fact]
    public void Laravel_storage_gets_modify()
    {
        var project = Folder("Sites", "crm");
        Touch("Sites", "crm", "artisan");
        Touch("Sites", "crm", "public", "index.php");
        var storage = Folder("Sites", "crm", "storage");
        var cache = Folder("Sites", "crm", "bootstrap", "cache");

        var plan = AclPlanner.Plan(_paths, [Site(Path.Combine(project, "public"))]);

        // Laravel reads its whole project (vendor, .env), not only public\.
        Assert.Contains(new AclGrant(project, AclRight.Read), plan);
        Assert.Contains(new AclGrant(storage, AclRight.Modify), plan);
        Assert.Contains(new AclGrant(cache, AclRight.Modify), plan);
        Assert.DoesNotContain(plan, g => g.Path == Path.Combine(project, "public"));
    }

    [Fact]
    public void WordPress_uploads_get_modify()
    {
        var blog = Folder("Sites", "blog");
        Touch("Sites", "blog", "wp-config.php");
        var uploads = Folder("Sites", "blog", "wp-content", "uploads");

        var plan = AclPlanner.Plan(_paths, [Site(blog)]);

        Assert.Contains(new AclGrant(blog, AclRight.Read), plan);
        Assert.Contains(new AclGrant(uploads, AclRight.Modify), plan);
    }

    [Fact]
    public void Symfony_var_gets_modify()
    {
        var project = Folder("Sites", "api");
        Touch("Sites", "api", "bin", "console");
        Touch("Sites", "api", "public", "index.php");
        var var = Folder("Sites", "api", "var");

        var plan = AclPlanner.Plan(_paths, [Site(Path.Combine(project, "public"))]);

        Assert.Contains(new AclGrant(project, AclRight.Read), plan);
        Assert.Contains(new AclGrant(var, AclRight.Modify), plan);
    }

    [Fact]
    public void Laravel_in_htdocs_gets_its_write_folders()
    {
        Touch("ArbiPache", "htdocs", "shop", "artisan");
        Touch("ArbiPache", "htdocs", "shop", "public", "index.php");
        var storage = Folder("ArbiPache", "htdocs", "shop", "storage");

        var plan = AclPlanner.Plan(_paths, []);

        Assert.Contains(new AclGrant(storage, AclRight.Modify), plan);
    }

    [Fact]
    public void Write_folders_that_do_not_exist_are_left_out()
    {
        var project = Folder("Sites", "crm");
        Touch("Sites", "crm", "artisan");
        Touch("Sites", "crm", "public", "index.php");

        var plan = AclPlanner.Plan(_paths, [Site(Path.Combine(project, "public"))]);

        Assert.DoesNotContain(plan, g => g.Right == AclRight.Modify && g.Path != _paths.Data);
    }

    [Fact]
    public void Proxy_sites_and_sites_inside_the_root_add_nothing()
    {
        var inside = Folder("ArbiPache", "htdocs", "shop");

        var plan = AclPlanner.Plan(_paths, [Site("http://127.0.0.1:5173", SiteType.Proxy), Site(inside)]);

        Assert.Equal(3, plan.Count);
    }
}
