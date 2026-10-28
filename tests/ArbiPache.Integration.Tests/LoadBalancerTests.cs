using System.Net;
using System.Net.Sockets;
using ArbiPache.Core.Apache;
using ArbiPache.Core.Components;
using ArbiPache.Core.Modes;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Php;
using ArbiPache.Core.Processes;
using ArbiPache.Core.Settings;
using ArbiPache.Core.Sites;
using ArbiPache.Integration.Tests.Support;

namespace ArbiPache.Integration.Tests;

// A site behind the client's load balancer: forwarded headers count only when they come from a listed proxy.
[Collection("apache-port-18080")]
public sealed class LoadBalancerTests : IAsyncDisposable
{
    private const int Port = 18080;
    private const string Host = "lb.example.com";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly AppPaths _paths = Runtimes.NewRoot("ap-it-lb");
    private readonly JobObject _job = new();
    private readonly ProcessSupervisor _supervisor;
    private readonly PhpPoolRunner _pools;

    public LoadBalancerTests()
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

    private async Task StartAsync(IReadOnlyList<string> trustedProxies, bool onlyFromProxies = false, string? htaccess = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var apache = await Runtimes.InstallAsync(_paths, ComponentKind.Apache, ct);
        await Runtimes.InstallAsync(_paths, ComponentKind.CaBundle, ct);
        var php = await Runtimes.InstallAsync(_paths, await Runtimes.LatestPhpAsync("8.5", ct), ct);
        new HttpdConfBootstrap(_paths).EnsureUserConf(apache);
        Runtimes.ListenOnLoopback(_paths, Port);
        new PhpIniBootstrap(_paths).EnsureIni(php.Version);

        var root = Path.Combine(_paths.Root, "projects", "lb");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "index.php"), "<?php echo $_SERVER['REMOTE_ADDR'].'|'.($_SERVER['HTTPS'] ?? '');");
        if (htaccess is not null) File.WriteAllText(Path.Combine(root, ".htaccess"), htaccess);

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

        var site = new SiteDefinition
        {
            Id = Host,
            Kind = SiteKind.Domain,
            Type = SiteType.Php,
            Name = Host,
            Root = root,
            Cert = CertSource.LoadBalancer,
            TrustedProxies = trustedProxies,
            AcceptOnlyFromProxies = onlyFromProxies,
        };
        var input = new RenderInput
        {
            Settings = settings,
            Stock = StockConfScanner.Scan(File.ReadAllText(_paths.HttpdConf)),
            Profile = BuiltInProfiles.Development,
            ApacheVersion = apache,
            Pools = pools,
            Sites = [site],
        };
        var tester = new ApacheConfigTester(_paths, () => apache);
        var outcome = await new ConfigApplier(new ConfigRenderer(_paths), new ConfigWriter(_paths), tester).ApplyAsync(input, ct);
        Assert.True(outcome.Ok, outcome.TestOutput);
        await new ApacheControl(_supervisor, _paths, tester, Http).StartAsync(apache, Port, ct);
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{Port}/");
        request.Headers.Host = Host;
        request.Headers.Add("X-Forwarded-For", "203.0.113.9");
        request.Headers.Add("X-Forwarded-Proto", "https");
        using var response = await Http.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Trusted_proxy_headers_honoured()
    {
        await StartAsync(["127.0.0.1"]);

        var (status, body) = await GetAsync();

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("203.0.113.9|on", body);
    }

    [Fact]
    public async Task Untrusted_proxy_headers_ignored()
    {
        await StartAsync(["10.9.9.9"]);

        var (status, body) = await GetAsync();

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("127.0.0.1|", body);
    }

    // Many apps ship a .htaccess with "Require all granted"; it must not lift the site's own restriction.
    [Fact]
    public async Task Htaccess_cannot_open_a_site_that_accepts_only_its_proxies()
    {
        await StartAsync(["10.9.9.9"], onlyFromProxies: true, htaccess: "Require all granted\r\n");

        var (status, _) = await GetAsync();

        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task Only_from_proxies_blocks_direct_client()
    {
        await StartAsync(["10.9.9.9"], onlyFromProxies: true);

        var (status, _) = await GetAsync();

        Assert.Equal(HttpStatusCode.Forbidden, status);
    }
}
