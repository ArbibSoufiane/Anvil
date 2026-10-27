using ArbiPache.Core.IO;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Php;

namespace ArbiPache.Core.Tests.Php;

public sealed class PhpExtensionManagerTests : IDisposable
{
    private const string Version = "8.4.27";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ap-phpext-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public PhpExtensionManagerTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
        var ext = Path.Combine(_paths.PhpRuntime(Version), "ext");
        Directory.CreateDirectory(ext);
        foreach (var name in new[] { "curl", "gd", "opcache", "xdebug" })
            File.WriteAllText(Path.Combine(ext, $"php_{name}.dll"), "");
        File.WriteAllText(Path.Combine(ext, "readme.txt"), "");
        Directory.CreateDirectory(_paths.PhpConfigDir(Version));
        WriteIni("[PHP]\r\nextension=curl\r\n;extension=gd\r\nzend_extension=opcache\r\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string IniFile => Path.Combine(_paths.PhpConfigDir(Version), "php.ini");

    private void WriteIni(string text) => File.WriteAllText(IniFile, text);

    [Fact]
    public void Lists_available_dlls_with_enabled_state()
    {
        var list = new PhpExtensionManager(_paths).List(Version);

        Assert.Equal(["curl", "gd", "opcache", "xdebug"], list.Select(e => e.Name));
        Assert.Equal(new PhpExtension("curl", Enabled: true, Available: true, IsZend: false, ManagedByMode: false), list[0]);
        Assert.Equal(new PhpExtension("gd", Enabled: false, Available: true, IsZend: false, ManagedByMode: false), list[1]);
        Assert.Equal(new PhpExtension("opcache", Enabled: true, Available: true, IsZend: true, ManagedByMode: false), list[2]);
    }

    [Fact]
    public void Enable_then_disable_round_trip()
    {
        var manager = new PhpExtensionManager(_paths);

        manager.Set(Version, "gd", enabled: true);
        Assert.True(manager.List(Version).Single(e => e.Name == "gd").Enabled);
        Assert.Contains("\r\nextension=gd\r\n", File.ReadAllText(IniFile), StringComparison.Ordinal);

        manager.Set(Version, "gd", enabled: false);
        manager.Set(Version, "opcache", enabled: false);
        Assert.Equal("[PHP]\r\nextension=curl\r\n;extension=gd\r\n;zend_extension=opcache\r\n", File.ReadAllText(IniFile));
    }

    [Fact]
    public void Legacy_dll_file_names_count_as_enabled()
    {
        WriteIni("[PHP]\r\nextension=php_gd.dll\r\n");
        var manager = new PhpExtensionManager(_paths);
        Assert.True(manager.List(Version).Single(e => e.Name == "gd").Enabled);

        manager.Set(Version, "gd", enabled: false);

        Assert.Empty(IniDocument.Parse(File.ReadAllText(IniFile)).GetAll("PHP", "extension"));
    }

    [Fact]
    public void Xdebug_loaded_by_the_mode_file_is_reported_on()
    {
        var confD = Path.Combine(_paths.PhpConfigDir(Version), "conf.d");
        Directory.CreateDirectory(confD);
        File.WriteAllText(Path.Combine(confD, "99-arbipache-mode.ini"),
            "[PHP]\r\ndisplay_errors = On\r\n\r\n[xdebug]\r\nzend_extension=\"D:/ArbiPache/runtime/xdebug/3.5.3-php8.4/php_xdebug-3.5.3-8.4-nts-vs17-x86_64.dll\"\r\n");
        File.Delete(Path.Combine(_paths.PhpRuntime(Version), "ext", "php_xdebug.dll"));

        var list = new PhpExtensionManager(_paths).List(Version);

        Assert.Equal(["curl", "gd", "opcache", "xdebug"], list.Select(e => e.Name));
        Assert.Equal(new PhpExtension("xdebug", Enabled: true, Available: true, IsZend: true, ManagedByMode: true), list[3]);
    }

    [Fact]
    public void Xdebug_off_when_the_mode_file_does_not_load_it()
    {
        var confD = Path.Combine(_paths.PhpConfigDir(Version), "conf.d");
        Directory.CreateDirectory(confD);
        File.WriteAllText(Path.Combine(confD, "99-arbipache-mode.ini"), "[PHP]\r\ndisplay_errors = Off\r\n");

        Assert.False(new PhpExtensionManager(_paths).List(Version).Single(e => e.Name == "xdebug").Enabled);
    }

    [Fact]
    public void Xdebug_reported_as_managed_by_mode()
    {
        var manager = new PhpExtensionManager(_paths);

        var xdebug = manager.List(Version).Single(e => e.Name == "xdebug");
        Assert.True(xdebug.ManagedByMode);
        Assert.True(xdebug.IsZend);
        Assert.Throws<InvalidOperationException>(() => manager.Set(Version, "xdebug", enabled: true));
    }

    [Fact]
    public void Enabling_a_missing_dll_is_refused()
    {
        var e = Assert.Throws<ArgumentException>(() => new PhpExtensionManager(_paths).Set(Version, "imagick", enabled: true));
        Assert.Contains("php_imagick.dll", e.Message, StringComparison.Ordinal);
    }
}
