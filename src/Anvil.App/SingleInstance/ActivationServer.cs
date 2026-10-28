using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace Anvil.App.SingleInstance;

/// <summary>
/// Listens for a second launch. Per connection: send own PID (so the caller can grant foreground rights),
/// read the caller's arguments, acknowledge, then run <paramref name="onActivate"/>.
/// </summary>
public sealed class ActivationServer(string pipeName, Func<string[], Task> onActivate) : IAsyncDisposable
{
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(5);
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;

    public Task StartAsync()
    {
        // The first pipe instance exists before StartAsync returns, so an immediate client cannot miss it.
        var pipe = Create();
        _loop = Task.Run(() => LoopAsync(pipe));
        return Task.CompletedTask;
    }

    private NamedPipeServerStream Create() =>
        new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task LoopAsync(NamedPipeServerStream pipe)
    {
        while (!_stop.IsCancellationRequested)
        {
            string[]? args = null;
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token);
                args = await ServeAsync(pipe);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException)
            {
                // A client that hangs up or sends garbage is dropped; the next one is still served.
            }

            await pipe.DisposeAsync();
            pipe = Create();
            if (args is not null) await onActivate(args);
        }
        await pipe.DisposeAsync();
    }

    private static async Task<string[]> ServeAsync(NamedPipeServerStream pipe)
    {
        using var timeout = new CancellationTokenSource(ClientTimeout);
        var pid = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(pid, Environment.ProcessId);
        await ActivationPipe.WriteFrameAsync(pipe, pid, timeout.Token);
        var payload = await ActivationPipe.ReadFrameAsync(pipe, timeout.Token);
        var args = JsonSerializer.Deserialize<string[]>(payload) ?? [];
        await pipe.WriteAsync(new byte[] { 1 }, timeout.Token);
        await pipe.FlushAsync(timeout.Token);
        return args;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try { await _loop; } catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
