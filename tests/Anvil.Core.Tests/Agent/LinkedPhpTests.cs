using Anvil.Agent;
using Anvil.Core.Agent;
using Anvil.Core.Components;
using Anvil.Core.IO;
using Anvil.Core.Paths;
using Anvil.Core.Php;
using Anvil.Core.Settings;
using Anvil.Core.Sites;

namespace Anvil.Core.Tests.Agent;

/// <summary>PHP versions the user already has, from pvm or any folder, used in place where they are.</summary>
public sealed class LinkedPhpTests : IDisposable
{
    private const string Apache = "2.4.69";

    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ap-linkphp-" + Guid.NewGuid().ToString("N")));
    private readonly string _home;
    private readonly List<string> _events = [];
    private readonly FakePools _pools;
    private readonly Dictionary<string, string> _versions = new(StringComparer.OrdinalIgnoreCase);

    public LinkedPhpTests()
    {
        _pools = new FakePools(_events);
        _home = Path.Combine(_paths.Root, "home");
        var runtime = _paths.ApacheRuntime(Apache);
        Directory.CreateDirectory(Path.Combine(runtime, "conf"));
        Directory.CreateDirectory(Path.Combine(runtime, "bin"));
        File.WriteAllText(Path.Combine(runtime, "conf", "httpd.conf"), "Define SRVROOT \"c:/Apache24\"\r\nServerRoot \"${SRVROOT}\"\r\nListen 80\r\n");
        File.WriteAllText(Path.Combine(runtime, "bin", "httpd.exe"), "");
        var ca = Path.Combine(_paths.Runtime, "cabundle", "2026-10-20");
        Directory.CreateDirectory(ca);
        File.WriteAllText(Path.Combine(ca, "cacert.pem"), "-----BEGIN CERTIFICATE-----");
        JsonFiles.Write(Path.Combine(_paths.Runtime, "installed.json"),
            new List<InstalledComponent> { new(ComponentKind.CaBundle, "2026-10-20", ca, new string('c', 64), DateTimeOffset.UtcNow) });
        new SettingsStore(_paths).Save(new AppSettings { ActiveApache = Apache, StartServicesOnLaunch = false });
    }

    public void Dispose()
    {
        try { Directory.Delete(_paths.Root, recursive: true); } catch (IOException) { }
    }

    private Orchestrator Make() =>
        new(_paths, new SettingsStore(_paths), new FakeTester(), new FakeApache(_events), _ => [],
            new ComponentCatalog([], new ShippedCatalogProvider(_paths)),
            new ComponentInstaller(_paths, new Downloader(new HttpClient()), _ => null), _pools, _ => true,
            userProfile: _home, phpVersionOf: exe => _versions.GetValueOrDefault(Path.GetDirectoryName(exe)!));

    // A 64-bit php-cgi.exe built with Visual Studio 2022 (linker 14.44), as pvm leaves it.
    private string PvmPhp(string name, string version, bool threadSafe = false)
    {
        var folder = Path.Combine(Pvm.VersionsDir(_home), name);
        Directory.CreateDirectory(Path.Combine(folder, "ext"));
        var image = new byte[0x200];
        "MZ"u8.CopyTo(image);
        image[0x3C] = 0x80;
        "PE\0\0"u8.CopyTo(image.AsSpan(0x80));
        image[0x84] = 0x64;
        image[0x85] = 0x86;
        image[0x80 + 24 + 2] = 14;
        image[0x80 + 24 + 3] = 44;
        File.WriteAllBytes(Path.Combine(folder, "php-cgi.exe"), image);
        File.WriteAllText(Path.Combine(folder, "php.ini-development"), "[PHP]\r\n;extension=intl\r\n");
        foreach (var e in PhpIniBootstrap.DefaultExtensions) File.WriteAllText(Path.Combine(folder, "ext", $"php_{e}.dll"), "");
        if (threadSafe) File.WriteAllText(Path.Combine(folder, "php8ts.dll"), "");
        _versions[folder] = version;
        return folder;
    }

    private IniDocument Ini(string version) => IniDocument.Parse(File.ReadAllText(Path.Combine(_paths.PhpConfigDir(version), "php.ini")));

    [Fact]
    public async Task Pvm_versions_show_which_ones_Anvil_has()
    {
        var ct = TestContext.Current.CancellationToken;
        var old = PvmPhp("php-8.4.23-Win32-vs17-x64", "8.4.23", threadSafe: true);
        var current = PvmPhp("php-8.4.27-nts-Win32-vs17-x64", "8.4.27");
        TestPhp.Install(_paths, "8.4.27");
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);

        var found = orchestrator.FindPvmPhp();

        Assert.Equal([new PhpFound("8.4.27", current, "vs17", ThreadSafe: false, InAnvil: true), new PhpFound("8.4.23", old, "vs17", ThreadSafe: true, InAnvil: false)],
            found);
    }

    [Fact]
    public async Task A_linked_php_gets_its_own_php_ini_and_becomes_the_default()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = PvmPhp("php-8.4.23-Win32-vs17-x64", "8.4.23", threadSafe: true);
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);

        var result = await orchestrator.LinkPhpAsync(folder, ct);

        Assert.True(result.Ok, result.Error);
        var php = Assert.Single(orchestrator.GetPhp());
        Assert.Equal(("8.4.23", folder, true, true, true), (php.Version, php.Folder, php.Linked, php.IsDefault, php.ThreadSafe));
        Assert.Equal(AppPaths.ForApache(Path.Combine(folder, "ext")), Ini("8.4.23").Get("PHP", "extension_dir"));
        Assert.False(File.Exists(Path.Combine(folder, "php.ini")));
        Assert.Equal("8.4.23", new SettingsStore(_paths).Load().DefaultPhp);
    }

    [Fact]
    public async Task A_linked_php_runs_from_its_own_folder()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = PvmPhp("php-8.4.23-Win32-vs17-x64", "8.4.23");
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);
        await orchestrator.LinkPhpAsync(folder, ct);

        await orchestrator.StartAsync(null, ct);

        Assert.Equal(Path.Combine(folder, "php-cgi.exe"), _pools.Running.Single().PhpCgi);
        Assert.Contains(orchestrator.GetExtensions("8.4.23"), e => e.Name == "intl");
    }

    [Fact]
    public async Task A_folder_that_is_not_php_says_why()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = Path.Combine(_paths.Root, "not-php");
        Directory.CreateDirectory(folder);
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);

        var result = await orchestrator.LinkPhpAsync(folder, ct);

        Assert.Equal($"{folder} has no php-cgi.exe, so it is not a PHP folder Anvil can run.", result.Error);
        Assert.Empty(orchestrator.GetPhp());
    }

    [Fact]
    public async Task A_version_Anvil_has_is_not_linked_again()
    {
        var ct = TestContext.Current.CancellationToken;
        TestPhp.Install(_paths, "8.4.27");
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);

        var result = await orchestrator.LinkPhpAsync(PvmPhp("php-8.4.27-nts-Win32-vs17-x64", "8.4.27"), ct);

        Assert.Equal("PHP 8.4.27 is already in Anvil.", result.Error);
    }

    [Fact]
    public async Task A_thread_safe_php_does_not_take_the_settings_of_a_non_thread_safe_one()
    {
        var ct = TestContext.Current.CancellationToken;
        TestPhp.Install(_paths, "8.4.20");
        var xdebug = Path.Combine(_paths.Runtime, "xdebug", "3.4.5-8.4-nts-vs17", "php_xdebug.dll");
        Directory.CreateDirectory(_paths.PhpConfigDir("8.4.20"));
        File.WriteAllText(Path.Combine(_paths.PhpConfigDir("8.4.20"), "php.ini"), $"[PHP]\r\nmemory_limit=2G\r\nzend_extension=\"{xdebug}\"\r\n");
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);

        var result = await orchestrator.LinkPhpAsync(PvmPhp("php-8.4.23-Win32-vs17-x64", "8.4.23", threadSafe: true), ct);

        Assert.True(result.Ok, result.Error);
        Assert.DoesNotContain("xdebug", File.ReadAllText(Path.Combine(_paths.PhpConfigDir("8.4.23"), "php.ini")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unlinking_keeps_the_folder()
    {
        var ct = TestContext.Current.CancellationToken;
        TestPhp.Install(_paths, "8.5.12");
        var store = new SettingsStore(_paths);
        store.Save(store.Load() with { DefaultPhp = "8.5.12" });
        var folder = PvmPhp("php-8.4.23-Win32-vs17-x64", "8.4.23");
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);
        await orchestrator.LinkPhpAsync(folder, ct);

        var result = await orchestrator.UnlinkPhpAsync("8.4.23", ct);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(["8.5.12"], orchestrator.GetPhp().Select(p => p.Version));
        Assert.True(File.Exists(Path.Combine(folder, "php-cgi.exe")));
    }

    [Fact]
    public async Task The_default_php_is_not_unlinked()
    {
        var ct = TestContext.Current.CancellationToken;
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);
        await orchestrator.LinkPhpAsync(PvmPhp("php-8.4.23-Win32-vs17-x64", "8.4.23"), ct);

        var result = await orchestrator.UnlinkPhpAsync("8.4.23", ct);

        Assert.Equal("PHP 8.4.23 is the default PHP. Make another version the default first.", result.Error);
        Assert.Single(orchestrator.GetPhp());
    }

    [Fact]
    public async Task A_php_a_site_uses_is_not_unlinked()
    {
        var ct = TestContext.Current.CancellationToken;
        TestPhp.Install(_paths, "8.5.12");
        var store = new SettingsStore(_paths);
        store.Save(store.Load() with { DefaultPhp = "8.5.12" });
        var docroot = Path.Combine(_paths.Root, "shop");
        Directory.CreateDirectory(docroot);
        new SiteStore(_paths).Save(new SiteDefinition
        {
            Id = "shop", Name = "shop.test", Kind = SiteKind.Domain, Type = SiteType.Php, Root = docroot, PhpVersion = "8.4.23",
        });
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);
        await orchestrator.LinkPhpAsync(PvmPhp("php-8.4.23-Win32-vs17-x64", "8.4.23"), ct);

        var result = await orchestrator.UnlinkPhpAsync("8.4.23", ct);

        Assert.Equal("shop.test uses PHP 8.4.23. Give it another PHP version first.", result.Error);
    }

    [Fact]
    public async Task A_downloaded_php_is_not_unlinked()
    {
        var ct = TestContext.Current.CancellationToken;
        TestPhp.Install(_paths, "8.5.12");
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);

        var result = await orchestrator.UnlinkPhpAsync("8.5.12", ct);

        Assert.Equal("PHP 8.5.12 was downloaded by Anvil, not added from a folder.", result.Error);
    }
}
