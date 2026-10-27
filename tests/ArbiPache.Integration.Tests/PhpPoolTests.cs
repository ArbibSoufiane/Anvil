using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ArbiPache.Core.Apache;
using ArbiPache.Core.Components;
using ArbiPache.Core.Modes;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Php;
using ArbiPache.Core.Processes;
using ArbiPache.Core.Settings;
using ArbiPache.Integration.Tests.Support;

namespace ArbiPache.Integration.Tests;

// Real Apache in front of a real PHP 8.5 pool: 2 workers that recycle after 2 requests each.
[Collection("apache-port-18080")]
public sealed class PhpPoolTests : IAsyncDisposable
{
    private const int Port = 18080;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly AppPaths _paths = Runtimes.NewRoot("ap-it-php");
    private readonly JobObject _job = new();
    private readonly ProcessSupervisor _supervisor;
    private readonly PhpPoolRunner _pools;

    public PhpPoolTests()
    {
        _supervisor = new ProcessSupervisor(_job, TimeProvider.System);
        _pools = new PhpPoolRunner(_supervisor, _paths);
    }

    public async ValueTask DisposeAsync()
    {
        await _pools.DisposeAsync();
        await _supervisor.DisposeAsync();
        _job.Dispose();
        try { Directory.Delete(_paths.Root, recursive: true); } catch (IOException) { }
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

    private async Task StartAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var apache = await Runtimes.InstallAsync(_paths, ComponentKind.Apache, ct);
        await Runtimes.InstallAsync(_paths, ComponentKind.CaBundle, ct);
        var php = await Runtimes.InstallAsync(_paths, await Runtimes.LatestPhpAsync("8.5", ct), ct);
        new HttpdConfBootstrap(_paths).EnsureUserConf(apache);
        File.WriteAllText(_paths.HttpdConf, File.ReadAllText(_paths.HttpdConf).Replace("\nListen 80\r", $"\nListen 127.0.0.1:{Port}\r"));
        new PhpIniBootstrap(_paths).EnsureIni(php.Version);

        File.WriteAllText(Path.Combine(_paths.Htdocs, "info.php"),
            "<?php echo $_SERVER['SCRIPT_FILENAME'], '|', $_SERVER['PATH_INFO'] ?? '', '|', $_SERVER['SCRIPT_NAME'];");
        File.WriteAllText(Path.Combine(_paths.Htdocs, "sleep.php"), "<?php usleep(200000); echo getmypid();");
        File.WriteAllText(Path.Combine(_paths.Htdocs, "busy.php"),
            "<?php file_put_contents(__DIR__ . '/busy.pid', getmypid()); sleep(20); echo 'finished';");

        var settings = new AppSettings
        {
            HttpPort = Port,
            HttpsPort = Port,
            ActiveApache = apache,
            DefaultPhp = php.Version,
            PhpPools = new Dictionary<string, PhpPoolSettings> { [php.Version] = new(2, 2) },
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
        };
        var tester = new ApacheConfigTester(_paths, () => apache);
        var outcome = await new ConfigApplier(new ConfigRenderer(_paths), new ConfigWriter(_paths), tester).ApplyAsync(input, ct);
        Assert.True(outcome.Ok, outcome.TestOutput);
        await new ApacheControl(_supervisor, _paths, tester, Http).StartAsync(apache, Port, ct);
    }

    private static Task<HttpResponseMessage> GetAsync(string path) =>
        Http.GetAsync($"http://127.0.0.1:{Port}{path}", TestContext.Current.CancellationToken);

    [Fact]
    public async Task Path_info_and_script_filename_are_correct()
    {
        await StartAsync();

        var response = await GetAsync("/info.php/foo/bar");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var parts = (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Split('|');
        Assert.Equal(AppPaths.ForApache(Path.Combine(_paths.Htdocs, "info.php")), parts[0], ignoreCase: true);
        Assert.Equal("/foo/bar", parts[1]);
        Assert.Equal("/info.php", parts[2]);
    }

    [Fact]
    public async Task Missing_php_file_is_not_found()
    {
        await StartAsync();

        var response = await GetAsync("/nope.php");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Requests_survive_recycling()
    {
        await StartAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => GetAsync("/sleep.php")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var pids = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        Assert.True(pids.Distinct().Count() > 2, $"workers never recycled: {string.Join(",", pids.Distinct())}");
    }

    [Fact]
    public async Task Killed_worker_fails_only_its_request()
    {
        await StartAsync();
        var marker = Path.Combine(_paths.Htdocs, "busy.pid");
        var busy = GetAsync("/busy.php");
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!File.Exists(marker) || new FileInfo(marker).Length == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "busy.php never started");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        var clock = Stopwatch.StartNew();

        using (var worker = Process.GetProcessById(int.Parse(File.ReadAllText(marker))))
            worker.Kill();

        var failed = await busy;
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"the killed request took {clock.Elapsed}");
        Assert.True((int)failed.StatusCode >= 500, $"killed request answered {failed.StatusCode}");
        var after = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => GetAsync("/sleep.php")));
        Assert.All(after, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }
}
