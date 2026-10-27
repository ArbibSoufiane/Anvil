using System.Buffers.Binary;
using System.Security.Principal;

namespace ArbiPache.App.SingleInstance;

public static class ActivationPipe
{
    public static string ForCurrentUser() => "ArbiPache.App." + WindowsIdentity.GetCurrent().User!.Value;

    private const int MaxFrame = 64 * 1024;

    internal static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken ct)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    internal static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 0 or > MaxFrame) throw new IOException($"Activation frame of {length} bytes rejected.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct);
        return payload;
    }
}
