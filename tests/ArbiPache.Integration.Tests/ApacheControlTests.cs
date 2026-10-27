using System.Net;
using System.Net.Sockets;
using ArbiPache.Core.Apache;
using ArbiPache.Core.Components;
using ArbiPache.Core.Modes;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Processes;
using ArbiPache.Core.Settings;
using ArbiPache.Integration.Tests.Support;

namespace ArbiPache.Integration.Tests;

// Apache binds 127.0.0.1:18080 only: no firewall prompt, no clash with anything on port 80.
[Collection("apache-port-18080")]
public sealed class ApacheControlTests : IAsyncDisposable
{
    private const int Port = 18080;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly AppPaths _paths = Runtimes.NewRoot("ap-it-ctl");
    private readonly JobObject _job = new();
    private readonly ProcessSupervisor _supervisor;
    private ApacheControl? _apache;

    public ApacheControlTests() => _supervisor = new ProcessSupervisor(_job, TimeProvider.System);

    public async ValueTask DisposeAsync()
    {
        await _supervisor.DisposeAsync();
        _job.Dispose();
        try { Directory.Delete(_paths.Root, recursive: true); } catch (IOException) { }
    }

    private async Task<(ApacheControl Apache, string Version)> PrepareAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var version = await Runtimes.InstallAsync(_paths, ComponentKind.Apache, ct);
        new HttpdConfBootstrap(_paths).EnsureUserConf(version);
        File.WriteAllText(_paths.HttpdConf, File.ReadAllText(_paths.HttpdConf).Replace("\nListen 80\r", $"\nListen 127.0.0.1:{Port}\r"));
        File.WriteAllText(Path.Combine(_paths.Htdocs, "index.html"), "hello from htdocs");

        var input = new RenderInput
        {
            Settings = new AppSettings { HttpPort = Port, HttpsPort = Port, ActiveApache = version },
            Stock = StockConfScanner.Scan(File.ReadAllText(_paths.HttpdConf)),
            Profile = BuiltInProfiles.Development,
            ApacheVersion = version,
        };
        var tester = new ApacheConfigTester(_paths, () => version);
        var outcome = await new ConfigApplier(new ConfigRenderer(_paths), new ConfigWriter(_paths), tester).ApplyAsync(input, ct);
        Assert.True(outcome.Ok, outcome.TestOutput);
        _apache = new ApacheControl(_supervisor, _paths, tester, Http);
        return (_apache, version);
    }

    private static async Task<string> GetAsync() =>
        await Http.GetStringAsync($"http://127.0.0.1:{Port}/", TestContext.Current.CancellationToken);

    [Fact]
    public async Task Start_serves_htdocs()
    {
        var (apache, version) = await PrepareAsync();

        await apache.StartAsync(version, Port, TestContext.Current.CancellationToken);

        Assert.Equal("hello from htdocs", await GetAsync());
        Assert.True(await apache.IsHealthyAsync(Port, TestContext.Current.CancellationToken));
        Assert.Equal(ProcessState.Running, _supervisor.Snapshot().Single(s => s.Name == "apache").State);
    }

    [Fact]
    public async Task Graceful_restart_keeps_serving()
    {
        var (apache, version) = await PrepareAsync();
        await apache.StartAsync(version, Port, TestContext.Current.CancellationToken);
        // A config change only shows after a reload: an alias that does not exist yet.
        var proof = Path.Combine(_paths.Root, "proof");
        Directory.CreateDirectory(proof);
        File.WriteAllText(Path.Combine(proof, "x.txt"), "reloaded");
        var fwd = AppPaths.ForApache(proof);
        File.WriteAllText(Path.Combine(_paths.Vhosts, "99-proof.conf"), $"Alias /proof \"{fwd}\"\r\n<Directory \"{fwd}\">\r\n    Require local\r\n</Directory>\r\n");
        var before = await Http.GetAsync($"http://127.0.0.1:{Port}/proof/x.txt", TestContext.Current.CancellationToken);
        var parent = _supervisor.Snapshot().Single(s => s.Name == "apache").Pid;

        await apache.GracefulRestartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(parent, _supervisor.Snapshot().Single(s => s.Name == "apache").Pid); // reloaded in place, not stop + start

        Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);
        Assert.Equal("reloaded", await Http.GetStringAsync($"http://127.0.0.1:{Port}/proof/x.txt", TestContext.Current.CancellationToken));
        Assert.Equal(ProcessState.Running, _supervisor.Snapshot().Single(s => s.Name == "apache").State);
    }

    [Fact]
    public async Task Stop_frees_port()
    {
        var (apache, version) = await PrepareAsync();
        await apache.StartAsync(version, Port, TestContext.Current.CancellationToken);

        await apache.StopAsync(TestContext.Current.CancellationToken);

        Assert.False(await apache.IsHealthyAsync(Port, TestContext.Current.CancellationToken));
        using var listener = new TcpListener(IPAddress.Loopback, Port);
        listener.Start();
        Assert.Equal(ProcessState.Stopped, _supervisor.Snapshot().Single(s => s.Name == "apache").State);
    }

    [Fact]
    public async Task Start_refuses_config_that_fails_the_test()
    {
        var (apache, version) = await PrepareAsync();
        File.AppendAllText(_paths.HttpdConf, "BogusDirective on\r\n");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => apache.StartAsync(version, Port, TestContext.Current.CancellationToken));

        Assert.Contains("BogusDirective", ex.Message);
        Assert.Empty(_supervisor.Snapshot());
    }
}
