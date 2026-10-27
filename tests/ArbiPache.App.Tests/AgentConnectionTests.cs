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
        });
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
