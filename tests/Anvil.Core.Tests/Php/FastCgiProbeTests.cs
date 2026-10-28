using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Anvil.Core.Php;

namespace Anvil.Core.Tests.Php;

public sealed class FastCgiProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Probe_reads_the_scripts_answer()
    {
        using var peer = FakeFastCgiPeer.Answering("Content-type: text/html\r\n\r\nanvil-probe-ok");

        Assert.True(await FastCgiProbe.ProbeAsync(peer.Port, @"C:\x\probe.php", TimeSpan.FromSeconds(2), Ct));
    }

    [Fact]
    public async Task Probe_sends_one_responder_request_for_the_script()
    {
        using var peer = FakeFastCgiPeer.Answering("anvil-probe-ok");

        await FastCgiProbe.ProbeAsync(peer.Port, @"C:\x\probe.php", TimeSpan.FromSeconds(2), Ct);
        await peer.Served;

        Assert.Equal(1, peer.Role);
        Assert.Equal(@"C:\x\probe.php", peer.Params["SCRIPT_FILENAME"]);
        Assert.Equal("GET", peer.Params["REQUEST_METHOD"]);
    }

    [Fact]
    public async Task Long_script_path_uses_four_byte_lengths()
    {
        using var peer = FakeFastCgiPeer.Answering("anvil-probe-ok");
        var script = @"C:\" + new string('x', 200) + @"\café\probe.php";

        Assert.True(await FastCgiProbe.ProbeAsync(peer.Port, script, TimeSpan.FromSeconds(2), Ct));
        await peer.Served;

        Assert.Equal(script, peer.Params["SCRIPT_FILENAME"]);
    }

    [Fact]
    public async Task Peer_that_closes_fails_the_probe()
    {
        using var peer = FakeFastCgiPeer.Closing();

        Assert.False(await FastCgiProbe.ProbeAsync(peer.Port, @"C:\x\probe.php", TimeSpan.FromSeconds(2), Ct));
    }

    [Fact]
    public async Task Other_output_fails_the_probe()
    {
        using var peer = FakeFastCgiPeer.Answering("Status: 404 Not Found\r\n\r\nNo input file specified.");

        Assert.False(await FastCgiProbe.ProbeAsync(peer.Port, @"C:\x\probe.php", TimeSpan.FromSeconds(2), Ct));
    }

    [Fact]
    public async Task Peer_that_never_answers_fails_the_probe_after_the_timeout()
    {
        using var peer = FakeFastCgiPeer.Silent();

        Assert.False(await FastCgiProbe.ProbeAsync(peer.Port, @"C:\x\probe.php", TimeSpan.FromMilliseconds(300), Ct));
    }

    [Fact]
    public async Task Nothing_listening_fails_the_probe()
    {
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        Assert.False(await FastCgiProbe.ProbeAsync(port, @"C:\x\probe.php", TimeSpan.FromSeconds(2), Ct));
    }

    /// <summary>A FastCGI server on loopback that takes one request, as php-cgi does, and records what it was sent.</summary>
    private sealed class FakeFastCgiPeer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        private FakeFastCgiPeer(Func<NetworkStream, FakeFastCgiPeer, Task> serve)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Served = Task.Run(async () =>
            {
                using var client = await _listener.AcceptTcpClientAsync();
                await serve(client.GetStream(), this);
            });
        }

        public int Port { get; }

        public Task Served { get; }

        public int Role { get; private set; }

        public Dictionary<string, string> Params { get; } = [];

        public static FakeFastCgiPeer Answering(string stdout) => new(async (stream, peer) =>
        {
            await peer.ReadRequestAsync(stream);
            await WriteAsync(stream, 6, Encoding.UTF8.GetBytes(stdout));
            await WriteAsync(stream, 6, []);
            await WriteAsync(stream, 3, new byte[8]);
        });

        public static FakeFastCgiPeer Closing() => new((_, _) => Task.CompletedTask);

        // Reads the request, then waits for the probe to hang up.
        public static FakeFastCgiPeer Silent() => new(async (stream, peer) =>
        {
            await peer.ReadRequestAsync(stream);
            try { await stream.ReadAtLeastAsync(new byte[1], 1, throwOnEndOfStream: false); } catch (IOException) { }
        });

        public void Dispose() => _listener.Stop();

        private async Task ReadRequestAsync(NetworkStream stream)
        {
            var header = new byte[8];
            while (true)
            {
                await stream.ReadExactlyAsync(header);
                var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
                var body = new byte[length + header[6]];
                await stream.ReadExactlyAsync(body);
                switch (header[1])
                {
                    case 1: Role = BinaryPrimitives.ReadUInt16BigEndian(body); break;
                    case 4: ReadPairs(body.AsSpan(0, length)); break;
                    case 5 when length == 0: return;
                }
            }
        }

        private void ReadPairs(ReadOnlySpan<byte> data)
        {
            while (data.Length > 0)
            {
                var name = ReadLength(ref data);
                var value = ReadLength(ref data);
                Params[Encoding.UTF8.GetString(data[..name])] = Encoding.UTF8.GetString(data.Slice(name, value));
                data = data[(name + value)..];
            }
        }

        private static int ReadLength(ref ReadOnlySpan<byte> data)
        {
            if ((data[0] & 0x80) == 0)
            {
                var one = data[0];
                data = data[1..];
                return one;
            }
            var four = (int)(BinaryPrimitives.ReadUInt32BigEndian(data) & 0x7FFFFFFF);
            data = data[4..];
            return four;
        }

        private static async Task WriteAsync(NetworkStream stream, byte type, byte[] content)
        {
            var header = new byte[] { 1, type, 0, 1, (byte)(content.Length >> 8), (byte)content.Length, 0, 0 };
            await stream.WriteAsync(header);
            await stream.WriteAsync(content);
        }
    }
}
