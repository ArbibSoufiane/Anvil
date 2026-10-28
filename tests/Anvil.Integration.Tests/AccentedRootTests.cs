using System.Net;
using System.Net.Sockets;
using Anvil.Core.Apache;
using Anvil.Core.Components;
using Anvil.Core.Modes;
using Anvil.Core.Paths;
using Anvil.Core.Php;
using Anvil.Core.Processes;
using Anvil.Core.Settings;
using Anvil.Core.Sites;
using Anvil.Integration.Tests.Support;

namespace Anvil.Integration.Tests;

// Anvil unzipped under a folder with accents and spaces, serving real requests through a real PHP pool.
[Collection("apache-port-18080")]
public sealed class AccentedRootTests : IAsyncDisposable
{
    private const int Port = 18080;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly AppPaths _paths = Runtimes.NewRoot("ap-it-Tëst Ärbi");
    private readonly JobObject _job = new();
    private readonly ProcessSupervisor _supervisor;
    private readonly PhpPoolRunner _pools;

    public AccentedRootTests()
    {
        _supervisor = new ProcessSupervisor(_job, TimeProvider.System);
        _pools = new PhpPoolRunner(_supervisor, _paths);
    }

    public async ValueTask DisposeAsync()
    {
        await _pools.DisposeAsync();
        await _supervisor.DisposeAsync();
        _job.Dispose();
        try { Directory.Delete(_paths.Root, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // rotatelogs may still be exiting
    }

    private static bool IsFree(int port)
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
        try
        {
            probe.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>htdocs with ini.php, a static domain site, and a PHP domain site under an accented folder.</summary>
    private async Task StartAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var apache = await Runtimes.InstallAsync(_paths, ComponentKind.Apache, ct);
        await Runtimes.InstallAsync(_paths, ComponentKind.CaBundle, ct);
        var php = await Runtimes.InstallAsync(_paths, await Runtimes.LatestPhpAsync("8.5", ct), ct);
        new HttpdConfBootstrap(_paths).EnsureUserConf(apache);
        Runtimes.ListenOnLoopback(_paths, Port);
        new PhpIniBootstrap(_paths).EnsureIni(php.Version);
        File.WriteAllText(PhpModeIniRenderer.FilePath(_paths, php.Version), PhpModeIniRenderer.Render(BuiltInProfiles.Production, php.Version, null));

        File.WriteAllText(Path.Combine(_paths.Htdocs, "ini.php"),
            "<?php echo ini_get('memory_limit'), '|', ini_get('max_execution_time'), '|', function_exists('exec') ? 'exec' : 'no exec';");
        var main = Path.Combine(_paths.Root, "projects", "vitrine");
        var demo = Path.Combine(_paths.Root, "projects", "démo app", "public");
        Directory.CreateDirectory(main);
        Directory.CreateDirectory(demo);
        File.WriteAllText(Path.Combine(main, "index.html"), "vitrine");
        File.WriteAllText(Path.Combine(demo, "index.php"), "<?php echo 'front|', $_SERVER['REQUEST_URI'], '|', $_SERVER['SCRIPT_NAME'];");
        SiteDefinition[] sites =
        [
            new() { Id = "vitrine.test", Kind = SiteKind.Domain, Type = SiteType.Static, Name = "vitrine.test", Root = main, Index = ["index.html"] },
            new() { Id = "demo.test", Kind = SiteKind.Domain, Type = SiteType.Php, Name = "demo.test", Root = demo },
        ];

        var settings = new AppSettings
        {
            HttpPort = Port,
            HttpsPort = Port,
            ActiveApache = apache,
            DefaultPhp = php.Version,
            PhpPools = new Dictionary<string, PhpPoolSettings> { [php.Version] = new(1, 100) },
        };
        var pools = new PhpPoolPlanner(_paths).Plan(settings, [php.Version], [php], IsFree);
        await _pools.ApplyAsync(pools, ct);

        var input = new RenderInput
        {
            Settings = settings,
            Stock = StockConfScanner.Scan(File.ReadAllText(_paths.HttpdConf)),
            Profile = BuiltInProfiles.Development,
            ApacheVersion = apache,
            Pools = pools,
            Sites = sites,
        };
        var tester = new ApacheConfigTester(_paths, () => apache);
        var outcome = await new ConfigApplier(new ConfigRenderer(_paths), new ConfigWriter(_paths), tester).ApplyAsync(input, ct);
        Assert.True(outcome.Ok, outcome.TestOutput);
        await new ApacheControl(_supervisor, _paths, tester, Http).StartAsync(apache, Port, ct);
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(string path, string? host = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{Port}{path}");
        if (host is not null) request.Headers.Host = $"{host}:{Port}";
        using var response = await Http.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    // PHPRC and PHP_INI_SCAN_DIR carry the accented path: when php-cgi reads them wrongly, the mode file's values are lost.
    [Fact]
    public async Task Php_reads_its_ini_files_under_an_accented_folder()
    {
        await StartAsync();

        Assert.Equal((HttpStatusCode.OK, "256M|30|no exec"), await GetAsync("/ini.php"));
    }

    [Fact]
    public async Task Domain_sites_under_an_accented_folder_answer()
    {
        await StartAsync();

        Assert.Equal((HttpStatusCode.OK, "vitrine"), await GetAsync("/", "vitrine.test"));
        Assert.Equal((HttpStatusCode.OK, "front|/|/index.php"), await GetAsync("/", "demo.test"));
    }
}
