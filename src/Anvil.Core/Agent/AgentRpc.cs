using System.IO.Pipes;
using System.Text.Json;
using Anvil.Core.IO;
using Anvil.Core.Preflight;
using StreamJsonRpc;
using Windows.Win32;

namespace Anvil.Core.Agent;

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

    /// <summary>
    /// Connects to the Agent's pipe, waiting up to <paramref name="timeout"/> for it to appear. With
    /// <paramref name="agentExe"/>, the process serving the pipe must be that program.
    /// </summary>
    /// <exception cref="TimeoutException">No Agent answered in time.</exception>
    /// <exception cref="InvalidOperationException">Another program serves the pipe.</exception>
    public static async Task<AgentClient> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken ct, string? agentExe = null)
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
            throw new TimeoutException($"The Anvil Agent did not answer on pipe {pipeName} within {timeout.TotalSeconds:0} s.");
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }

        if (agentExe is not null && Stranger(pipe, agentExe) is { } other)
        {
            await pipe.DisposeAsync();
            throw new InvalidOperationException($"Another program answers on the Anvil Agent's pipe: {other}. "
                + "If it is another copy of Anvil, close it, then open this one again.");
        }

        var rpc = Create(pipe);
        var api = rpc.Attach<IAgentApi>();
        rpc.StartListening();
        return new AgentClient(rpc, api);
    }

    /// <summary>The program serving the pipe when it is not <paramref name="agentExe"/>; null when it is.</summary>
    private static string? Stranger(NamedPipeClientStream pipe, string agentExe)
    {
        if (!PInvoke.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) return "a program Windows does not name";
        var image = PortProbe.ImagePath((int)pid);
        return image is not null && SameFile(image, agentExe) ? null : image ?? $"process {pid}";
    }

    /// <summary>
    /// Compares the files themselves: Windows names a running program by its real path, while this copy may have been
    /// started through a junction or a subst drive.
    /// </summary>
    private static bool SameFile(string a, string b)
    {
        if (string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            using var first = File.OpenHandle(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var second = File.OpenHandle(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return PInvoke.GetFileInformationByHandle(first, out var x) && PInvoke.GetFileInformationByHandle(second, out var y)
                && x.dwVolumeSerialNumber == y.dwVolumeSerialNumber && x.nFileIndexHigh == y.nFileIndexHigh && x.nFileIndexLow == y.nFileIndexLow;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
