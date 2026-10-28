using Anvil.Core.Components;
using Anvil.Core.IO;
using Anvil.Core.Paths;
using Anvil.Core.Php;

namespace Anvil.Core.Tests.Php;

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
        Assert.Equal(new PhpExtension("curl", Enabled: true, Available: true, IsZend: false), list[0]);
        Assert.Equal(new PhpExtension("gd", Enabled: false, Available: true, IsZend: false), list[1]);
        Assert.Equal(new PhpExtension("opcache", Enabled: true, Available: true, IsZend: true), list[2]);
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

    private IReadOnlyList<InstalledComponent> InstallXdebugFor(string minor)
    {
        var dir = Path.Combine(_paths.Runtime, "xdebug", $"3.5.3-php{minor}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"php_xdebug-3.5.3-{minor}-nts-vs17-x86_64.dll"), "");
        return [new InstalledComponent(ComponentKind.Xdebug, "3.5.3", dir, "", DateTimeOffset.UnixEpoch, AppliesTo: minor)];
    }

    [Fact]
    public void Xdebug_is_an_ordinary_extension()
    {
        var manager = new PhpExtensionManager(_paths, InstallXdebugFor("8.4"));

        manager.Set(Version, "xdebug", enabled: true);

        var ini = IniDocument.Parse(File.ReadAllText(IniFile));
        Assert.StartsWith(AppPaths.ForApache(Path.Combine(_paths.Runtime, "xdebug", "3.5.3-php8.4")), Assert.Single(ini.GetAll("xdebug", "zend_extension")), StringComparison.Ordinal);
        Assert.Contains("zend_extension=\"", File.ReadAllText(IniFile), StringComparison.Ordinal);
        Assert.Contains("xdebug.mode = debug,develop", File.ReadAllText(IniFile), StringComparison.Ordinal);
        Assert.Equal("trigger", ini.Get("xdebug", "xdebug.start_with_request"));
        var xdebug = manager.List(Version).Single(e => e.Name == "xdebug");
        Assert.True(xdebug.Enabled);
        Assert.True(xdebug.IsZend);

        manager.Set(Version, "xdebug", enabled: false);

        var after = IniDocument.Parse(File.ReadAllText(IniFile));
        Assert.Empty(after.GetAll("xdebug", "zend_extension"));
        Assert.Null(after.Get("xdebug", "xdebug.mode"));
        Assert.False(manager.List(Version).Single(e => e.Name == "xdebug").Enabled);
        Assert.Contains("zend_extension=opcache", File.ReadAllText(IniFile), StringComparison.Ordinal);
    }

    [Fact]
    public void Xdebug_for_another_branch_does_not_count()
    {
        File.Delete(Path.Combine(_paths.PhpRuntime(Version), "ext", "php_xdebug.dll"));
        var manager = new PhpExtensionManager(_paths, InstallXdebugFor("8.5"));

        Assert.DoesNotContain(manager.List(Version), e => e.Name == "xdebug");
        var e = Assert.Throws<ArgumentException>(() => manager.Set(Version, "xdebug", enabled: true));
        Assert.Equal("No Xdebug build is installed for PHP 8.4. Install it from the PHP page first.", e.Message);
    }

    [Fact]
    public void Xdebug_still_loaded_by_php_ini_is_reported_on()
    {
        File.Delete(Path.Combine(_paths.PhpRuntime(Version), "ext", "php_xdebug.dll"));
        WriteIni("[PHP]\r\nextension=curl\r\n\r\n[xdebug]\r\nzend_extension=\"D:/Anvil/runtime/xdebug/3.5.3-php8.4/php_xdebug-3.5.3-8.4-nts-vs17-x86_64.dll\"\r\n");

        var xdebug = new PhpExtensionManager(_paths).List(Version).Single(e => e.Name == "xdebug");

        Assert.True(xdebug.Enabled);
        Assert.True(xdebug.IsZend);
    }

    [Fact]
    public void Enabling_a_missing_dll_is_refused()
    {
        var e = Assert.Throws<ArgumentException>(() => new PhpExtensionManager(_paths).Set(Version, "imagick", enabled: true));
        Assert.Contains("php_imagick.dll", e.Message, StringComparison.Ordinal);
    }
}
