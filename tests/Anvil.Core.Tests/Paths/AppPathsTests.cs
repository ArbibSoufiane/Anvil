using Anvil.Core.Database;
using Anvil.Core.Paths;

namespace Anvil.Core.Tests.Paths;

public sealed class AppPathsTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "ap-paths-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
    }

    [Fact]
    public void Paths_are_under_root()
    {
        var paths = new AppPaths(@"D:\AP");

        Assert.Equal(@"D:\AP\data\conf", paths.Conf);
        Assert.Equal(@"D:\AP\data\apache\httpd.conf", paths.HttpdConf);
        Assert.Equal(@"D:\AP\runtime\php\8.4.14", paths.PhpRuntime("8.4.14"));
        Assert.Equal(@"D:\AP\data\php\8.4.14", paths.PhpConfigDir("8.4.14"));
        Assert.Equal(@"D:\AP\runtime\mariadb\11.4.8", paths.DbRuntime(DbEngine.MariaDb, "11.4.8"));
        Assert.Equal(@"D:\AP\data\mysql", paths.DbData(DbEngine.MySql));
    }

    [Fact]
    public void ForApache_uses_forward_slashes()
    {
        Assert.Equal("D:/Mes Sites/café", AppPaths.ForApache(@"D:\Mes Sites\café"));
    }

    [Fact]
    public void RootChanged_detects_move()
    {
        var a = new AppPaths(Path.Combine(_temp, "A"));
        var b = new AppPaths(Path.Combine(_temp, "B"));
        a.EnsureCreated();
        b.EnsureCreated();
        a.MarkRoot();
        File.Copy(Path.Combine(a.Data, ".root"), Path.Combine(b.Data, ".root"));

        Assert.False(a.RootChanged());
        Assert.True(b.RootChanged());
    }

    [Fact]
    public void RootChanged_true_when_marker_missing()
    {
        var a = new AppPaths(Path.Combine(_temp, "A"));
        a.EnsureCreated();

        Assert.True(a.RootChanged());
    }

    [Fact]
    public void EnsureCreated_makes_all_folders()
    {
        var p = new AppPaths(Path.Combine(_temp, "root"));

        p.EnsureCreated();

        foreach (var dir in new[] { p.Runtime, p.Catalog, p.Htdocs, p.Data, p.Conf, p.Vhosts, p.Sites,
                     p.ApacheConfDir, p.PhpConfRoot, p.Certs, p.Logs, p.Backups, p.Run,
                     p.Tmp, p.Secrets })
            Assert.True(Directory.Exists(dir), dir);
    }
}
