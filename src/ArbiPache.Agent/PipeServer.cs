using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using ArbiPache.Core.Agent;
using Serilog;
using StreamJsonRpc;

namespace ArbiPache.Agent;

/// <summary>
/// Serves <see cref="IAgentApi"/> on a named pipe, one JSON-RPC connection per client. Only the user who started the
/// Agent, Administrators and the listed extra SIDs may connect.
/// </summary>
public sealed class PipeServer(string pipeName, IAgentApi api, IReadOnlyList<string> extraSids) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<JsonRpc, byte> _connections = new();
    private Task _accept = Task.CompletedTask;

    /// <summary>Creates the first pipe instance (failing if another process already owns the name) and accepts clients.</summary>
    public void Start() => _accept = AcceptLoopAsync(Create(firstInstance: true));

    public static PipeSecurity CreateSecurity(IEnumerable<string> extraSids)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        // Clients get read/write only: CreateNewInstance stays with the owner, so nobody else can serve this pipe name.
        const PipeAccessRights client = PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), client, AccessControlType.Allow));
        foreach (var sid in extraSids)
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid), client, AccessControlType.Allow));
        return security;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _accept;
        foreach (var rpc in _connections.Keys) rpc.Dispose();
        _stop.Dispose();
    }

    private NamedPipeServerStream Create(bool firstInstance) =>
        NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None),
            0, 0, CreateSecurity(extraSids));

    private async Task AcceptLoopAsync(NamedPipeServerStream pipe)
    {
        while (true)
        {
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token);
                Serve(pipe);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                return;
            }
            catch (IOException e)
            {
                Log.Warning(e, "A client of pipe {Pipe} failed to connect", pipeName);
                await pipe.DisposeAsync();
            }
            pipe = Create(firstInstance: false);
        }
    }

    private void Serve(NamedPipeServerStream pipe)
    {
        var rpc = AgentRpc.Create(pipe);
        rpc.AddLocalRpcTarget<IAgentApi>(api, null);
        _connections[rpc] = 0;
        _ = rpc.Completion.ContinueWith(_ =>
        {
            _connections.TryRemove(rpc, out var _);
            rpc.Dispose();
        }, TaskScheduler.Default);
        rpc.StartListening();
    }
}
