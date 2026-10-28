using System.Net;
using System.Net.Sockets;
using Anvil.Core.Paths;
using Anvil.Core.Php;
using Anvil.Core.Processes;

namespace Anvil.Core.Tests.Php;

/// <summary>Runs the pool with the test child standing in for php-cgi: it answers each connection on its stdin socket.</summary>
public sealed class PhpPoolRunnerTests : IAsyncDisposable
{
    private static readonly string Child = Path.Combine(AppContext.BaseDirectory, "Anvil.TestChild.exe");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ap-pool-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly ProcessSupervisor _supervisor = new(new JobObject(), TimeProvider.System);
    private readonly PhpPoolRunner _runner;

    public PhpPoolRunnerTests()
    {
        _paths = new AppPaths(_root);
        _runner = new PhpPoolRunner(_supervisor, _paths);
    }

    public async ValueTask DisposeAsync()
    {
        await _runner.DisposeAsync();
        await _supervisor.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // Below the ephemeral range: tests that bind port 0 in parallel can never be handed a port these tests just closed.
    private static int _nextPort = 19100 + Random.Shared.Next(800);

    private static int FreePort()
    {
        while (true)
        {
            var port = Interlocked.Increment(ref _nextPort);
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
            try
            {
                probe.Bind(new IPEndPoint(IPAddress.Loopback, port));
                return port;
            }
            catch (SocketException)
            {
            }
        }
    }

    private PhpPool Pool(int port, int workers) => new("8.5.12", "php85", port, workers, 1000, Child, _root, _root);

    private static async Task<string> AskAsync(int port)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        using var reader = new StreamReader(client.GetStream());
        return await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken) ?? "";
    }

    // Under load Windows can take a moment to tear the listener down; a leaked handle would hold it for good.
    private static async Task<bool> ClosedSoonAsync(int port)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
            }
            catch (SocketException)
            {
                return true;
            }
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        return false;
    }

    private IReadOnlyDictionary<string, ProcessState> States() =>
        _supervisor.Snapshot().ToDictionary(s => s.Name, s => s.State);

    [Fact]
    public async Task Apply_starts_workers_sharing_one_socket()
    {
        var port = FreePort();

        await _runner.ApplyAsync([Pool(port, 2)], TestContext.Current.CancellationToken);

        Assert.Equal(ProcessState.Running, States()["php-php85-1"]);
        Assert.Equal(ProcessState.Running, States()["php-php85-2"]);
        var pids = _supervisor.Snapshot().Select(s => $"hello from {s.Pid}").ToList();
        Assert.Contains(await AskAsync(port), pids);
    }

    [Fact]
    public async Task Fewer_workers_stops_the_extra_ones()
    {
        var port = FreePort();
        await _runner.ApplyAsync([Pool(port, 2)], TestContext.Current.CancellationToken);

        await _runner.ApplyAsync([Pool(port, 1)], TestContext.Current.CancellationToken);

        Assert.Equal(ProcessState.Running, States()["php-php85-1"]);
        Assert.Equal(ProcessState.Stopped, States()["php-php85-2"]);
        Assert.StartsWith("hello from", await AskAsync(port), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_a_pool_closes_its_socket()
    {
        var port = FreePort();
        await _runner.ApplyAsync([Pool(port, 1)], TestContext.Current.CancellationToken);

        await _runner.ApplyAsync([], TestContext.Current.CancellationToken);

        Assert.Equal(ProcessState.Stopped, States()["php-php85-1"]);
        Assert.True(await ClosedSoonAsync(port));
    }

    [Fact]
    public async Task Other_children_do_not_inherit_the_pool_socket()
    {
        var port = FreePort();
        await _runner.ApplyAsync([Pool(port, 1)], TestContext.Current.CancellationToken);
        // Started like httpd: Process.Start with redirected output, which inherits every inheritable handle.
        _supervisor.Start(new ProcessSpec("bystander", Child, ["--sleep"], new Dictionary<string, string>(), _root, Path.Combine(_root, "bystander.log")));

        await _runner.ApplyAsync([], TestContext.Current.CancellationToken);

        Assert.True(await ClosedSoonAsync(port));
    }

    [Fact]
    public async Task Restart_replaces_every_worker_and_keeps_the_socket()
    {
        var port = FreePort();
        await _runner.ApplyAsync([Pool(port, 2)], TestContext.Current.CancellationToken);
        var before = _supervisor.Snapshot().Select(s => s.Pid).ToHashSet();

        await _runner.RestartAsync("php85", TestContext.Current.CancellationToken);

        Assert.All(_supervisor.Snapshot(), s =>
        {
            Assert.Equal(ProcessState.Running, s.State);
            Assert.DoesNotContain(s.Pid, before);
        });
        Assert.StartsWith("hello from", await AskAsync(port), StringComparison.Ordinal);
    }

    [Fact]
    public async Task State_reports_running_pools_and_stopped_unknown_ones()
    {
        var port = FreePort();
        await _runner.ApplyAsync([Pool(port, 2)], TestContext.Current.CancellationToken);

        Assert.Equal(ProcessState.Running, _runner.StateOf("php85"));
        Assert.Equal(ProcessState.Stopped, _runner.StateOf("php84"));
        Assert.Equal(["php85"], _runner.Running.Select(p => p.Key));

        await _runner.ApplyAsync([], TestContext.Current.CancellationToken);

        Assert.Equal(ProcessState.Stopped, _runner.StateOf("php85"));
        Assert.Empty(_runner.Running);
    }

    [Fact]
    public async Task Port_change_moves_the_pool()
    {
        var oldPort = FreePort();
        await _runner.ApplyAsync([Pool(oldPort, 1)], TestContext.Current.CancellationToken);
        var newPort = FreePort();

        await _runner.ApplyAsync([Pool(newPort, 1)], TestContext.Current.CancellationToken);

        Assert.StartsWith("hello from", await AskAsync(newPort), StringComparison.Ordinal);
        Assert.True(_runner.Owns(newPort));
        Assert.False(_runner.Owns(oldPort));
    }
}
