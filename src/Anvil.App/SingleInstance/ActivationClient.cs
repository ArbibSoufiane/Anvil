using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using Windows.Win32;

namespace Anvil.App.SingleInstance;

public static class ActivationClient
{
    /// <summary>Hands <paramref name="args"/> to the running instance. False when nobody answers in time.</summary>
    public static async Task<bool> SendAsync(string pipeName, string[] args, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(cts.Token);
            var pid = BinaryPrimitives.ReadInt32LittleEndian(await ActivationPipe.ReadFrameAsync(pipe, cts.Token));
            PInvoke.AllowSetForegroundWindow((uint)pid);
            await ActivationPipe.WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(args), cts.Token);
            var ack = new byte[1];
            await pipe.ReadExactlyAsync(ack, cts.Token);
            return ack[0] == 1;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
