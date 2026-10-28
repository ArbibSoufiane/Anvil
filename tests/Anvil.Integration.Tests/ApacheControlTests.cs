using System.Net;
using System.Net.Sockets;
using Anvil.Core.Apache;
using Anvil.Core.Components;
using Anvil.Core.Modes;
using Anvil.Core.Paths;
using Anvil.Core.Preflight;
using Anvil.Core.Processes;
using Anvil.Core.Settings;
using Anvil.Core.Sites;
using Anvil.Integration.Tests.Support;

namespace Anvil.Integration.Tests;

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
        try { Directory.Delete(_paths.Root, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // rotatelogs may still be exiting
    }

    private async Task<(ApacheControl Apache, string Version)> PrepareAsync(
        ServerMode mode = ServerMode.Development, IReadOnlyList<SiteDefinition>? sites = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var version = await Runtimes.InstallAsync(_paths, ComponentKind.Apache, ct);
        new HttpdConfBootstrap(_paths).EnsureUserConf(version);
        Runtimes.ListenOnLoopback(_paths, Port);
        File.WriteAllText(Path.Combine(_paths.Htdocs, "index.html"), "hello from htdocs");

        var input = new RenderInput
        {
            Settings = new AppSettings { Mode = mode, HttpPort = Port, HttpsPort = Port, ActiveApache = version },
            Stock = StockConfScanner.Scan(File.ReadAllText(_paths.HttpdConf)),
            Profile = BuiltInProfiles.For(mode),
            ApacheVersion = version,
            Sites = sites ?? [],
        };
        var tester = new ApacheConfigTester(_paths, () => version);
        var outcome = await new ConfigApplier(new ConfigRenderer(_paths), new ConfigWriter(_paths), tester).ApplyAsync(input, ct);
        Assert.True(outcome.Ok, outcome.TestOutput);
        _apache = new ApacheControl(_supervisor, _paths, tester, Http, new PortProbe());
        return (_apache, version);
    }

    private static async Task<string> GetAsync() =>
        await Http.GetStringAsync($"http://127.0.0.1:{Port}/", TestContext.Current.CancellationToken);

    private SiteDefinition StaticSite(SiteKind kind, string name, string body)
    {
        var root = Path.Combine(_paths.Root, "sites", SiteSet.IdFor(kind, name));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "index.html"), body);
        return new SiteDefinition { Id = SiteSet.IdFor(kind, name), Kind = kind, Type = SiteType.Static, Name = name, Root = root, Index = ["index.html"] };
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetAsHostAsync(string host, string path = "/")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{Port}{path}");
        request.Headers.Host = $"{host}:{Port}";
        using var response = await Http.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Host_header_picks_the_site_in_production()
    {
        var (apache, version) = await PrepareAsync(ServerMode.Production,
        [
            StaticSite(SiteKind.Domain, "shop.test", "hello from shop"),
            StaticSite(SiteKind.Domain, "blog.test", "hello from blog"),
        ]);

        await apache.StartAsync(version, Port, TestContext.Current.CancellationToken);

        Assert.Equal((HttpStatusCode.OK, "hello from shop"), await GetAsHostAsync("shop.test"));
        Assert.Equal((HttpStatusCode.OK, "hello from blog"), await GetAsHostAsync("blog.test"));
        Assert.Equal((HttpStatusCode.OK, "hello from htdocs"), await GetAsHostAsync("[::1]"));
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsHostAsync("unknown.example")).Status);
    }

    // A site saved before Anvil refused such folders, or one whose .htaccess grants everything, still cannot serve data\.
    [Fact]
    public async Task Site_over_the_anvil_folder_never_serves_data()
    {
        File.WriteAllText(Path.Combine(_paths.Root, ".htaccess"), "Require all granted\r\n");
        File.WriteAllText(Path.Combine(_paths.Data, ".htaccess"), "Require all granted\r\n");
        File.WriteAllText(Path.Combine(_paths.Data, "secret.txt"), "secret");
        File.WriteAllText(Path.Combine(_paths.Root, "index.html"), "hello from shop");
        var site = new SiteDefinition
        {
            Id = SiteSet.IdFor(SiteKind.Domain, "shop.test"), Kind = SiteKind.Domain, Type = SiteType.Static, Name = "shop.test",
            Root = _paths.Root, Index = ["index.html"],
        };
        var (apache, version) = await PrepareAsync(ServerMode.Production, [site]);

        await apache.StartAsync(version, Port, TestContext.Current.CancellationToken);

        Assert.Equal((HttpStatusCode.OK, "hello from shop"), await GetAsHostAsync("shop.test"));
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsHostAsync("shop.test", "/data/secret.txt")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsHostAsync("shop.test", "/DATA/secret.txt")).Status);
    }

    // Another program answering on the port is not Apache serving: the start fails and names that program.
    [Fact]
    public async Task Start_fails_when_another_program_answers_on_the_port()
    {
        var ct = TestContext.Current.CancellationToken;
        var (apache, version) = await PrepareAsync();
        using var other = new TcpListener(IPAddress.Loopback, Port) { ExclusiveAddressUse = true };
        other.Start();
        using var answering = new CancellationTokenSource();
        var responder = Task.Run(async () =>
        {
            while (!answering.IsCancellationRequested)
            {
                using var client = await other.AcceptTcpClientAsync(answering.Token);
                try
                {
                    var stream = client.GetStream();
                    if (await stream.ReadAsync(new byte[4096], answering.Token) == 0) continue;
                    await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), answering.Token);
                }
                catch (IOException)
                {
                    // The probe gave up on this connection.
                }
            }
        }, answering.Token);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => apache.StartAsync(version, Port, ct));

        Assert.Contains($"Port {Port} is used by ", error.Message);
        Assert.Contains($"(PID {Environment.ProcessId}), so Apache cannot listen on it.", error.Message);
        await answering.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => responder);
    }

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
