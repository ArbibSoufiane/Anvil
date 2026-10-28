using System.Net;
using System.Net.Sockets;
using Anvil.Core.Components;
using Anvil.Core.Paths;
using Anvil.Core.Php;
using Anvil.Core.Settings;

namespace Anvil.Core.Tests.Php;

public sealed class PhpPoolPlannerTests
{
    private static readonly AppPaths Paths = new(@"D:\Anvil");
    private static readonly AppSettings Settings = new();
    private static readonly IReadOnlyList<InstalledComponent> Installed =
        new[] { "8.5.12", "8.4.27", "8.3.36" }.Select(v => Php(v)).ToList();

    private static InstalledComponent Php(string version) =>
        new(ComponentKind.Php, version, Paths.PhpRuntime(version), new string('0', 64), DateTimeOffset.UnixEpoch);

    private static IReadOnlyList<PhpPool> Plan(AppSettings s, string[] required, IReadOnlyList<InstalledComponent>? installed = null, Func<int, bool>? isFree = null) =>
        new PhpPoolPlanner(Paths).Plan(s, required, installed ?? Installed, isFree ?? (_ => true));

    [Fact]
    public void Only_required_versions_get_pools()
    {
        var pools = Plan(Settings, ["8.5.12", "7.4.33"]);

        var pool = Assert.Single(pools);
        Assert.Equal("8.5.12", pool.Version);
        Assert.Equal("php85", pool.Key);
        Assert.Equal(Path.Combine(Paths.PhpRuntime("8.5.12"), "php-cgi.exe"), pool.PhpCgi);
        Assert.Equal(Paths.PhpConfigDir("8.5.12"), pool.IniDir);
        Assert.Equal(Path.Combine(Paths.PhpConfigDir("8.5.12"), "conf.d"), pool.ScanDir);
    }

    [Fact]
    public void Ports_stable_across_plans()
    {
        var first = Plan(Settings, ["8.4.27", "8.5.12"]);
        var saved = Settings with { PhpPoolPorts = PhpPoolPlanner.Allocations(first) };

        var second = Plan(saved, ["8.5.12", "8.4.27"]);

        Assert.Equal(first.ToDictionary(p => p.Key, p => p.Port), second.ToDictionary(p => p.Key, p => p.Port));
        Assert.Equal(first.Single(p => p.Key == "php85").Port, Plan(saved, ["8.5.12"]).Single().Port);
    }

    [Fact]
    public void Busy_port_skipped()
    {
        var pool = Plan(Settings, ["8.5.12"], isFree: p => p != 9100).Single();

        Assert.Equal(9101, pool.Port);
    }

    [Fact]
    public void Ports_never_shared_between_versions()
    {
        var saved = Settings with { PhpPoolPorts = new Dictionary<string, int> { ["php84"] = 9100 } };

        var pool = Plan(saved, ["8.5.12"]).Single();

        Assert.Equal(9101, pool.Port);
    }

    [Fact]
    public void Default_workers_half_cores()
    {
        var pool = Plan(Settings, ["8.5.12"]).Single();
        Assert.Equal(Suggest.PhpWorkers(Environment.ProcessorCount), pool.Workers);
        Assert.Equal(1000, pool.MaxRequests);

        var tuned = Settings with { PhpPools = new Dictionary<string, PhpPoolSettings> { ["8.5.12"] = new(6, 50) } };
        var custom = Plan(tuned, ["8.5.12"]).Single();
        Assert.Equal((6, 50), (custom.Workers, custom.MaxRequests));
    }

    [Fact]
    public void Same_minor_versions_get_distinct_keys()
    {
        var installed = new[] { Php("8.5.12"), Php("8.5.5") };

        var pools = Plan(Settings, ["8.5.12", "8.5.5"], installed);

        Assert.Equal(["php8512", "php855"], pools.Select(p => p.Key).Order());
        Assert.Equal(2, pools.Select(p => p.Port).Distinct().Count());
    }

    [Fact]
    public void Worker_env_has_phprc_scan_dir_and_max_requests()
    {
        var pool = Plan(Settings with { PhpPools = new Dictionary<string, PhpPoolSettings> { ["8.5.12"] = new(3, 500) } }, ["8.5.12"]).Single();
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        var specs = PhpPoolRunner.WorkerSpecs(pool, socket, Paths);

        Assert.Equal(["php-php85-1", "php-php85-2", "php-php85-3"], specs.Select(s => s.Name));
        Assert.All(specs, s =>
        {
            Assert.Equal(pool.PhpCgi, s.Exe);
            Assert.Empty(s.Args);
            Assert.Same(socket, s.StdinListenSocket);
            Assert.Equal(pool.IniDir, s.Env["PHPRC"]);
            Assert.Equal(pool.ScanDir, s.Env["PHP_INI_SCAN_DIR"]);
            Assert.Equal("500", s.Env["PHP_FCGI_MAX_REQUESTS"]);
            Assert.Equal("0", s.Env["PHP_FCGI_CHILDREN"]);
            Assert.Equal(Path.Combine(Paths.Logs, "php", "php85-workers.log"), s.LogFile);
        });
    }

    [Fact]
    public void Version_key_is_php_plus_major_minor()
    {
        Assert.Equal("php85", PhpVersionKey.Of("8.5.5"));
        Assert.Equal("php74", PhpVersionKey.Of("7.4.33"));
        Assert.Equal("php8512", PhpVersionKey.Exact("8.5.12"));
    }

    [Fact]
    public void Loopback_is_the_only_address()
    {
        Assert.Equal(IPAddress.Loopback, PhpPoolRunner.BindAddress);
    }
}
