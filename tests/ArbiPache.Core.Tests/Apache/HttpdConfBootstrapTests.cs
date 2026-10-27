using ArbiPache.Core.Apache;
using ArbiPache.Core.Paths;

namespace ArbiPache.Core.Tests.Apache;

public sealed class HttpdConfBootstrapTests : IDisposable
{
    private const string Stock = "# Apache conf\r\nDefine SRVROOT \"C:/Apache24\"\r\nServerRoot \"${SRVROOT}\"\r\nListen 80\r\n";
    private const string Begin = "# >>> ArbiPache >>>";
    private const string End = "# <<< ArbiPache <<<";

    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ap-httpd-" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, recursive: true);
    }

    private static int Count(string text, string part) => text.Split(part).Length - 1;

    [Fact]
    public void Managed_block_added_once()
    {
        var once = HttpdConfBootstrap.ApplyManagedLines(Stock, "D:/AP/runtime/apache/2.4.69", "D:/AP/data/conf/arbipache.conf");
        var twice = HttpdConfBootstrap.ApplyManagedLines(once, "D:/AP/runtime/apache/2.4.69", "D:/AP/data/conf/arbipache.conf");

        Assert.Equal(once, twice);
        Assert.Equal(1, Count(twice, Begin));
        Assert.EndsWith($"{Begin}\r\nInclude \"D:/AP/data/conf/arbipache.conf\"\r\n{End}\r\n", twice);
    }

    [Fact]
    public void SRVROOT_replaced_with_forward_slashes()
    {
        var result = HttpdConfBootstrap.ApplyManagedLines(Stock, @"D:\Mes Sites\AP\runtime\apache\2.4.69", "D:/AP/data/conf/arbipache.conf");

        Assert.Contains("Define SRVROOT \"D:/Mes Sites/AP/runtime/apache/2.4.69\"\r\n", result);
        Assert.DoesNotContain("C:/Apache24", result);
    }

    [Fact]
    public void Lines_outside_block_preserved_byte_identical()
    {
        const string conf = "Define SRVROOT \"D:/AP/runtime/apache/2.4.69\"\r\n# my tweak\r\n  KeepAlive On\r\n\r\nLoadModule foo_module modules/mod_foo.so\r\n";

        var result = HttpdConfBootstrap.ApplyManagedLines(conf, "D:/AP/runtime/apache/2.4.69", "D:/AP/data/conf/arbipache.conf");

        Assert.StartsWith(conf, result);
        Assert.Equal(conf + $"{Begin}\r\nInclude \"D:/AP/data/conf/arbipache.conf\"\r\n{End}\r\n", result);
    }

    [Fact]
    public void Block_repaired_when_root_moved()
    {
        var old = HttpdConfBootstrap.ApplyManagedLines(Stock, "C:/Old/runtime/apache/2.4.69", "C:/Old/data/conf/arbipache.conf");
        var withUserLine = old + "# added after the block\r\n";

        var moved = HttpdConfBootstrap.ApplyManagedLines(withUserLine, "E:/New/runtime/apache/2.4.69", "E:/New/data/conf/arbipache.conf");

        Assert.DoesNotContain("C:/Old", moved);
        Assert.Contains("# added after the block\r\n", moved);
        Assert.Equal(1, Count(moved, Begin));
        Assert.EndsWith($"Include \"E:/New/data/conf/arbipache.conf\"\r\n{End}\r\n", moved);
    }

    [Fact]
    public void Missing_SRVROOT_is_added_at_top()
    {
        var result = HttpdConfBootstrap.ApplyManagedLines("Listen 80\n", "D:/AP/runtime/apache/2.4.69", "D:/AP/data/conf/arbipache.conf");

        Assert.StartsWith("Define SRVROOT \"D:/AP/runtime/apache/2.4.69\"\nListen 80\n", result);
        Assert.EndsWith($"{End}\n", result);
    }

    [Fact]
    public void EnsureUserConf_copies_stock_once_and_applies_block()
    {
        var stockDir = Path.Combine(_paths.ApacheRuntime("2.4.69"), "conf");
        Directory.CreateDirectory(stockDir);
        File.WriteAllText(Path.Combine(stockDir, "httpd.conf"), Stock);
        var bootstrap = new HttpdConfBootstrap(_paths);

        bootstrap.EnsureUserConf("2.4.69");
        File.AppendAllText(_paths.HttpdConf, "# user edit\r\n");
        bootstrap.EnsureUserConf("2.4.69");

        var conf = File.ReadAllText(_paths.HttpdConf);
        Assert.Contains($"Define SRVROOT \"{AppPaths.ForApache(_paths.ApacheRuntime("2.4.69"))}\"", conf);
        Assert.Contains("# user edit", conf);
        Assert.Contains($"Include \"{AppPaths.ForApache(Path.Combine(_paths.Conf, "arbipache.conf"))}\"", conf);
        Assert.Equal(1, Count(conf, Begin));
    }
}
