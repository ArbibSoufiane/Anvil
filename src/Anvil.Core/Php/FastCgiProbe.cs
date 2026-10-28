using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Anvil.Core.Php;

/// <summary>
/// Asks a php-cgi pool to run one script over FastCGI 1.0, as Apache's mod_proxy_fcgi does: a pool that listens may still
/// fail every request (a missing DLL loaded per request, a broken php.ini).
/// </summary>
public static class FastCgiProbe
{
    /// <summary>The probe script's file name in data\run.</summary>
    public const string ScriptName = "probe.php";

    public const string Script = "<?php echo 'anvil-probe-ok';";

    private const string Marker = "anvil-probe-ok";
    private const byte BeginRequest = 1, EndRequest = 3, Params = 4, Stdin = 5, Stdout = 6;

    // A broken pool may print a page of errors; the marker is near the start or not there at all.
    private const int MaxOutput = 64 * 1024;

    /// <summary>
    /// True when the pool on 127.0.0.1:<paramref name="port"/> runs <paramref name="scriptPath"/> and prints
    /// <c>anvil-probe-ok</c> before it ends the request, within <paramref name="timeout"/>.
    /// </summary>
    public static async Task<bool> ProbeAsync(int port, string scriptPath, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, limit.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(Request(scriptPath), limit.Token);

            var stdout = new MemoryStream();
            var header = new byte[8];
            while (true)
            {
                await stream.ReadExactlyAsync(header, limit.Token);
                var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
                var content = new byte[length + header[6]];
                await stream.ReadExactlyAsync(content, limit.Token);
                if (header[1] == Stdout && stdout.Length < MaxOutput) stdout.Write(content, 0, length);
                else if (header[1] == EndRequest) return Encoding.UTF8.GetString(stdout.ToArray()).Contains(Marker, StringComparison.Ordinal);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false; // No answer within the timeout.
        }
        catch (Exception e) when (e is SocketException or IOException)
        {
            return false; // Refused, reset, or closed before the end of the request.
        }
    }

    /// <summary>BEGIN_REQUEST as a responder that closes the connection after, the parameters, then empty STDIN.</summary>
    private static byte[] Request(string scriptPath)
    {
        var pairs = new MemoryStream();
        Pair(pairs, "SCRIPT_FILENAME", scriptPath);
        Pair(pairs, "REQUEST_METHOD", "GET");

        var request = new MemoryStream();
        Record(request, BeginRequest, [0, 1, 0, 0, 0, 0, 0, 0]);
        Record(request, Params, pairs.ToArray());
        Record(request, Params, []);
        Record(request, Stdin, []);
        return request.ToArray();
    }

    // Header: version 1, type, request id 1, content length, no padding, reserved.
    private static void Record(Stream to, byte type, ReadOnlySpan<byte> content)
    {
        Span<byte> header = [1, type, 0, 1, 0, 0, 0, 0];
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], checked((ushort)content.Length));
        to.Write(header);
        to.Write(content);
    }

    private static void Pair(Stream to, string name, string value)
    {
        var n = Encoding.UTF8.GetBytes(name);
        var v = Encoding.UTF8.GetBytes(value);
        Length(to, n.Length);
        Length(to, v.Length);
        to.Write(n);
        to.Write(v);
    }

    // Under 128 bytes a length takes one byte; longer, four with the top bit set.
    private static void Length(Stream to, int length)
    {
        if (length < 128)
        {
            to.WriteByte((byte)length);
            return;
        }
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(four, (uint)length | 0x8000_0000);
        to.Write(four);
    }
}
