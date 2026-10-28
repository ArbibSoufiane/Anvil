using System.IO.Pipes;
using ArbiPache.App.Services;
using ArbiPache.App.Tests.Support;
using ArbiPache.Core.Agent;
using ArbiPache.Core.Paths;
using StreamJsonRpc;

namespace ArbiPache.App.Tests;

public sealed class AgentConnectionTests
{
    [Fact]
    public async Task Starts_agent_when_missing_and_reconnects_after_it_restarts()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipe = "ArbiPache.Agent.Test." + Guid.NewGuid().ToString("N");
        var api = new FakeAgentApi();
        TestServer? server = null;
        var starts = 0;
        await using var connection = new AgentConnection(new AppPaths(Path.GetTempPath()), pipe, () =>
        {
            starts++;
            server = TestServer.Start(pipe, api);
            return true;
        }, agentExe: Environment.ProcessPath);
        var reconnected = new TaskCompletionSource();
        connection.Reconnected += (_, _) => reconnected.TrySetResult();

        var proxy = await connection.ConnectAsync(ct);
        var received = new TaskCompletionSource<AgentStatus>();
        proxy.StatusChanged += (_, s) => received.TrySetResult(s);
        Assert.Equal("4.0.0", (await proxy.GetStatusAsync(ct)).AgentVersion);
        Assert.Equal(1, starts);

        await server!.DisposeAsync();
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        // The client can connect before the new server has subscribed to the Agent's events.
        await server!.Listening.WaitAsync(TimeSpan.FromSeconds(30), ct);
        api.Raise(api.Status with { AgentVersion = "4.0.1" });

        Assert.Equal(2, starts);
        Assert.Equal("4.0.1", (await received.Task.WaitAsync(TimeSpan.FromSeconds(5), ct)).AgentVersion);
        Assert.Equal("4.0.1", (await proxy.GetStatusAsync(ct)).AgentVersion);
    }

    [Fact]
    public async Task A_held_connection_starts_no_agent_until_released()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipe = "ArbiPache.Agent.Test." + Guid.NewGuid().ToString("N");
        var starts = 0;
        TestServer? server = null;
        await using var connection = new AgentConnection(new AppPaths(Path.GetTempPath()), pipe, () =>
        {
            starts++;
            server = TestServer.Start(pipe, new FakeAgentApi());
            return true;
        }, agentExe: Environment.ProcessPath);
        IAgentLink link = connection;

        using (link.HoldStart())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => link.ConnectAsync(ct));
            Assert.Equal(0, starts);
        }
        await link.ConnectAsync(ct);

        Assert.Equal(1, starts);
        await server!.DisposeAsync();
    }

    [Fact]
    public async Task The_agent_is_gone_once_its_mutex_is()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = @"Local\ArbiPache.Agent.Test." + Guid.NewGuid().ToString("N");
        var mutex = new Mutex(initiallyOwned: false, name);
        await using var connection = new AgentConnection(new AppPaths(Path.GetTempPath()), "unused", () => false, name);

        var gone = ((IAgentLink)connection).WhenAgentGoneAsync(ct);
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        Assert.False(gone.IsCompleted);
        mutex.Dispose();

        await gone.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    [Theory]
    [InlineData(true, "start")]
    [InlineData(false, "child")]
    public void An_installed_service_is_started_instead_of_a_child(bool installed, string expected)
    {
        var calls = new List<string>();
        var service = new FakeAgentService(calls) { Installed = installed };

        Assert.True(AgentConnection.StartAgent(service, () =>
        {
            calls.Add("child");
            return true;
        }));

        Assert.Equal([expected], calls);
    }

    [Fact]
    public void A_service_windows_will_not_start_counts_as_not_started()
    {
        var service = new FakeAgentService([]) { Installed = true, StartFails = new InvalidOperationException("Access is denied.") };

        Assert.False(AgentConnection.StartAgent(service, () => true));
    }

    /// <summary>A one-client Agent stand-in on a real pipe.</summary>
    private sealed class TestServer : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private JsonRpc? _rpc;
        private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Listening => _listening.Task;

        private TestServer(string name) =>
            _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        public static TestServer Start(string name, IAgentApi api)
        {
            var server = new TestServer(name);
            _ = server.AcceptAsync(api);
            return server;
        }

        private async Task AcceptAsync(IAgentApi api)
        {
            await _pipe.WaitForConnectionAsync();
            _rpc = AgentRpc.Create(_pipe);
            _rpc.AddLocalRpcTarget<IAgentApi>(api, null);
            _rpc.StartListening();
            _listening.TrySetResult();
        }

        public async ValueTask DisposeAsync()
        {
            _rpc?.Dispose();
            await _pipe.DisposeAsync();
        }
    }
}
