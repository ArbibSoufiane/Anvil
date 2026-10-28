using Anvil.Core.Components;
using Anvil.Core.Database;
using Anvil.Core.Paths;
using Anvil.Core.Php;
using Anvil.Core.Settings;
using Anvil.Core.Tests.Agent;
using K = Anvil.Core.Components.ComponentKind;

namespace Anvil.Core.Tests.Components;

public sealed class ComponentUpdaterTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ap-update-" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        try { Directory.Delete(_paths.Root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private static InstalledComponent Installed(ComponentKind k, string v) =>
        new(k, v, $@"D:\Anvil\runtime\{k}\{v}", new string('a', 64), DateTimeOffset.UnixEpoch);

    private static ComponentRelease Release(ComponentKind k, string v) => new(k, v, new Uri($"https://example.com/{k}-{v}.zip"), new string('b', 64), null);

    private static readonly AppSettings Active = new() { ActiveApache = "2.4.69", DefaultPhp = "8.5.5", DbEngine = DbEngine.MariaDb, DbVersion = "11.8.3" };

    [Fact]
    public void Offers_only_newer_versions()
    {
        InstalledComponent[] installed = [Installed(K.Apache, "2.4.69"), Installed(K.Php, "8.4.10"), Installed(K.Php, "8.5.5"), Installed(K.MariaDb, "11.8.3")];
        ComponentRelease[] available =
        [
            Release(K.Apache, "2.4.68"), Release(K.Apache, "2.4.69"), Release(K.Apache, "2.4.70"), Release(K.Apache, "2.5.0"),
            Release(K.Php, "8.4.10"), Release(K.Php, "8.5.3"), Release(K.Php, "8.5.12"), Release(K.Php, "8.6.0"),
            Release(K.MariaDb, "11.4.9"), Release(K.MariaDb, "11.8.5"), Release(K.MariaDb, "12.0.1"), Release(K.MySql, "8.4.13"),
        ];

        var offers = ComponentUpdater.Offers(Active, ["8.4.10", "8.5.5"], installed, available);

        Assert.Equal(
        [
            new UpdateOffer(K.Apache, "2.4.69", "2.4.70", available[2]),
            new UpdateOffer(K.Php, "8.5.5", "8.5.12", available[6]),
            new UpdateOffer(K.MariaDb, "11.8.3", "11.8.5", available[9]),
        ], offers);
    }

    [Fact]
    public void An_installed_newer_patch_is_offered_without_a_download()
    {
        InstalledComponent[] installed = [Installed(K.Apache, "2.4.69"), Installed(K.Apache, "2.4.70"), Installed(K.Php, "8.5.5"), Installed(K.Php, "8.5.12")];

        var offers = ComponentUpdater.Offers(Active with { DbEngine = null, DbVersion = null }, ["8.5.5"], installed, [Release(K.Php, "8.5.12")]);

        Assert.Equal([new UpdateOffer(K.Apache, "2.4.69", "2.4.70", null), new UpdateOffer(K.Php, "8.5.5", "8.5.12", null)], offers);
    }

    [Fact]
    public void Nothing_in_use_has_nothing_to_update()
    {
        Assert.Empty(ComponentUpdater.Offers(new AppSettings(), [], [], [Release(K.Apache, "2.4.70"), Release(K.Php, "8.5.12"), Release(K.MariaDb, "11.8.5")]));
    }

    [Fact]
    public void Php_patch_update_carries_ini()
    {
        TestPhp.Install(_paths, "8.5.5");
        TestPhp.Install(_paths, "8.5.12");
        var ini = new PhpIniBootstrap(_paths);
        ini.EnsureIni("8.5.5");
        var oldDir = _paths.PhpConfigDir("8.5.5");
        File.AppendAllText(Path.Combine(oldDir, "php.ini"), "memory_limit = 2G\r\n");
        File.WriteAllText(Path.Combine(oldDir, "conf.d", "50-mine.ini"), "xdebug.mode = debug\r\n");

        Assert.Equal("8.5.5", ComponentUpdater.CarryPhpConfig(_paths, "8.5.12", ["8.4.10", "8.5.5", "8.5.12"]));
        ini.EnsureIni("8.5.12");

        var newIni = File.ReadAllText(Path.Combine(_paths.PhpConfigDir("8.5.12"), "php.ini"));
        Assert.Contains("memory_limit = 2G", newIni);
        Assert.Contains(AppPaths.ForApache(Path.Combine(_paths.PhpRuntime("8.5.12"), "ext")), newIni);
        Assert.DoesNotContain(AppPaths.ForApache(_paths.PhpRuntime("8.5.5")), newIni);
        Assert.Equal("xdebug.mode = debug\r\n", File.ReadAllText(Path.Combine(_paths.PhpConfigDir("8.5.12"), "conf.d", "50-mine.ini")));
        Assert.Contains(AppPaths.ForApache(_paths.PhpRuntime("8.5.5")), File.ReadAllText(Path.Combine(oldDir, "php.ini")));
    }

    [Fact]
    public void A_version_with_its_own_ini_or_no_older_patch_gets_nothing_carried()
    {
        TestPhp.Install(_paths, "8.4.10");
        TestPhp.Install(_paths, "8.5.12");
        new PhpIniBootstrap(_paths).EnsureIni("8.4.10");

        Assert.Null(ComponentUpdater.CarryPhpConfig(_paths, "8.5.12", ["8.4.10", "8.5.12"]));

        new PhpIniBootstrap(_paths).EnsureIni("8.5.12");
        TestPhp.Install(_paths, "8.5.5");
        new PhpIniBootstrap(_paths).EnsureIni("8.5.5");
        File.AppendAllText(Path.Combine(_paths.PhpConfigDir("8.5.5"), "php.ini"), "memory_limit = 2G\r\n");

        Assert.Null(ComponentUpdater.CarryPhpConfig(_paths, "8.5.12", ["8.5.5", "8.5.12"]));
        Assert.DoesNotContain("memory_limit = 2G", File.ReadAllText(Path.Combine(_paths.PhpConfigDir("8.5.12"), "php.ini")));
    }
}
