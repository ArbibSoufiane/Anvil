using System.IO.Pipes;
using System.Text.Json;
using ArbiPache.Core.IO;
using StreamJsonRpc;

namespace ArbiPache.Core.Agent;

/// <summary>Both ends of the Agent pipe: the same framing and JSON options on each side.</summary>
public static class AgentRpc
{
    public static JsonRpc Create(Stream stream)
    {
        var formatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions = new JsonSerializerOptions(JsonFiles.Options) { WriteIndented = false },
        };
        return new JsonRpc(new HeaderDelimitedMessageHandler(stream, stream, formatter));
    }

    /// <summary>Connects to the Agent's pipe, waiting up to <paramref name="timeout"/> for it to appear.</summary>
    /// <exception cref="TimeoutException">No Agent answered in time.</exception>
    public static async Task<AgentClient> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            await pipe.ConnectAsync(limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await pipe.DisposeAsync();
            throw new TimeoutException($"The ArbiPache Agent did not answer on pipe {pipeName} within {timeout.TotalSeconds:0} s.");
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }

        var rpc = Create(pipe);
        var api = rpc.Attach<IAgentApi>();
        rpc.StartListening();
        return new AgentClient(rpc, api);
    }
}

/// <summary>One connection to the Agent. Completion ends when the pipe closes.</summary>
public sealed class AgentClient(JsonRpc rpc, IAgentApi api) : IAsyncDisposable
{
    public IAgentApi Api { get; } = api;
    public Task Completion => rpc.Completion;

    public ValueTask DisposeAsync()
    {
        rpc.Dispose();
        return ValueTask.CompletedTask;
    }
}
