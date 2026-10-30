using System.Net.Sockets;
using System.Text;

namespace Anvil.Core.Redis;

/// <summary>Whether Redis answered PING, and if not, what went wrong in a sentence.</summary>
public sealed record RedisPing(bool Ok, string Message);

/// <summary>Connects to Redis, logs in when there is a password and sends PING, speaking just enough of its protocol.</summary>
public static class RedisProbe
{
    public static async Task<RedisPing> PingAsync(string host, int port, string? password, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cts.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

            if (!string.IsNullOrEmpty(password))
            {
                await SendAsync(stream, cts.Token, "AUTH", password);
                var auth = await reader.ReadLineAsync(cts.Token);
                if (auth != "+OK") return new(false, $"Redis did not accept the password: {Reply(auth)}");
            }

            await SendAsync(stream, cts.Token, "PING");
            var ping = await reader.ReadLineAsync(cts.Token);
            return ping == "+PONG"
                ? new(true, $"Redis answers on {host}:{port}.")
                : new(false, $"Redis answered PING with {Reply(ping)}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, $"Nothing answered on {host}:{port} within {timeout.TotalSeconds:0} seconds.");
        }
        catch (Exception e) when (e is SocketException or IOException)
        {
            return new(false, $"Could not connect to {host}:{port}: {e.Message}");
        }
    }

    // A RESP array of bulk strings, as redis-cli sends.
    private static async Task SendAsync(Stream stream, CancellationToken ct, params string[] parts)
    {
        var command = new StringBuilder($"*{parts.Length}\r\n");
        foreach (var part in parts) command.Append($"${Encoding.UTF8.GetByteCount(part)}\r\n{part}\r\n");
        await stream.WriteAsync(Encoding.UTF8.GetBytes(command.ToString()), ct);
    }

    private static string Reply(string? line) => line is null ? "nothing (the connection closed)." : line.TrimStart('-', '+') + ".";
}
