using System.Net;
using System.Net.Sockets;
using Serilog;

namespace Anvil.Core.Network;

/// <summary>
/// A UDP name service on each LAN address: every name under the network domain resolves to the address the query came
/// in on, any other name is refused, and only loopback and private-network askers get an answer.
/// </summary>
public sealed class DnsResponder(int port = 53) : INameService, IAsyncDisposable
{
    // Windows reports an ICMP "port unreachable" for an earlier reply as an error on the next receive unless this is off.
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    private readonly SemaphoreSlim _apply = new(1, 1);
    private readonly Dictionary<IPAddress, Listener> _listeners = [];
    private volatile string? _domain;

    public IReadOnlyList<IPAddress> Listening
    {
        get
        {
            lock (_listeners) return _listeners.Keys.ToList();
        }
    }

    /// <summary>Loopback or a private IPv4 network: nobody beyond the local network gets an answer.</summary>
    public static bool Accepts(IPAddress asker) => IPAddress.IsLoopback(asker) || LanAddresses.IsPrivate(asker);

    public async Task<IReadOnlyList<DnsBindFailure>> ApplyAsync(string? networkDomain, IReadOnlyList<IPAddress> addresses, CancellationToken ct)
    {
        await _apply.WaitAsync(ct);
        try
        {
            _domain = networkDomain;
            var wanted = networkDomain is null ? [] : addresses.Distinct().ToList();
            List<Listener> stale;
            lock (_listeners)
            {
                stale = _listeners.Where(l => !wanted.Contains(l.Key)).Select(l => l.Value).ToList();
                foreach (var listener in stale) _listeners.Remove(listener.Address);
            }

            foreach (var listener in stale) await listener.DisposeAsync();

            var failures = new List<DnsBindFailure>();
            foreach (var address in wanted)
            {
                lock (_listeners)
                {
                    if (_listeners.ContainsKey(address)) continue;
                }

                try
                {
                    var listener = new Listener(address, Bind(address), this);
                    lock (_listeners) _listeners[address] = listener;
                }
                catch (SocketException e)
                {
                    Log.Warning("The name service cannot listen on {Address}:{Port}: {Reason}", address, port, e.Message);
                    failures.Add(new DnsBindFailure(address, e.Message));
                }
            }

            return failures;
        }
        finally
        {
            _apply.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ApplyAsync(null, [], CancellationToken.None);
        _apply.Dispose();
    }

    private Socket Bind(IPAddress address)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
        try
        {
            socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            socket.Bind(new IPEndPoint(address, port));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>One socket and its receive loop. Only cancellation ends the loop; a bad packet or a failed send does not.</summary>
    private sealed class Listener : IAsyncDisposable
    {
        private readonly Socket _socket;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public Listener(IPAddress address, Socket socket, DnsResponder owner)
        {
            Address = address;
            _socket = socket;
            _loop = Task.Run(() => ReceiveAsync(owner, _stop.Token));
        }

        public IPAddress Address { get; }

        private async Task ReceiveAsync(DnsResponder owner, CancellationToken ct)
        {
            var buffer = new byte[4096];
            EndPoint anyone = new IPEndPoint(IPAddress.Any, 0);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, anyone, ct);
                    if (received.RemoteEndPoint is not IPEndPoint asker || !Accepts(asker.Address)) continue;
                    if (DnsMessage.TryParse(buffer.AsSpan(0, received.ReceivedBytes)) is not { } question) continue;
                    if (owner._domain is not { } domain) continue;

                    var (code, withAddress) = DnsPolicy.Decide(question, domain);
                    var reply = DnsMessage.Reply(question, code, withAddress ? Address : null, DnsPolicy.Ttl);
                    await _socket.SendToAsync(reply, SocketFlags.None, asker, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    Log.Warning(e, "The name service on {Address} could not answer a query", Address);
                    await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken.None);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _socket.Dispose();
            try
            {
                await _loop;
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }

            _stop.Dispose();
        }
    }
}
