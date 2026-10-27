using System.Globalization;
using System.Net;
using System.Net.Sockets;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Processes;

namespace ArbiPache.Core.Php;

/// <summary>
/// Runs PHP pools the way PHP-FPM does: the agent owns one listening socket per pool and every php-cgi
/// worker accept()s on it. A worker that recycles or crashes leaves the socket open, so requests wait
/// in its backlog for the next free worker instead of failing.
/// </summary>
public sealed class PhpPoolRunner(ProcessSupervisor sup, AppPaths paths) : IAsyncDisposable
{
    public static readonly IPAddress BindAddress = IPAddress.Loopback;
    private const int Backlog = 511;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Running> _running = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Running(PhpPool Pool, Socket Socket);

    public static IReadOnlyList<ProcessSpec> WorkerSpecs(PhpPool pool, Socket socket, AppPaths paths)
    {
        var env = new Dictionary<string, string>
        {
            ["PHPRC"] = pool.IniDir,
            ["PHP_INI_SCAN_DIR"] = pool.ScanDir,
            ["PHP_FCGI_MAX_REQUESTS"] = pool.MaxRequests.ToString(CultureInfo.InvariantCulture),
            ["PHP_FCGI_CHILDREN"] = "0",
        };
        var log = Path.Combine(paths.Logs, "php", pool.Key + "-workers.log");
        var dir = Path.GetDirectoryName(pool.PhpCgi)!;
        return Enumerable.Range(1, pool.Workers)
            .Select(n => new ProcessSpec(WorkerName(pool.Key, n), pool.PhpCgi, [], env, dir, log, StdinListenSocket: socket))
            .ToList();
    }

    /// <summary>True when one of the running pools listens on <paramref name="port"/>.</summary>
    public bool Owns(int port)
    {
        lock (_running) return _running.Values.Any(r => r.Pool.Port == port);
    }

    /// <summary>Starts, resizes, restarts and removes pools so that exactly <paramref name="pools"/> run.</summary>
    public async Task ApplyAsync(IReadOnlyList<PhpPool> pools, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var wanted = pools.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, running) in Snapshot())
                if (!wanted.TryGetValue(key, out var next) || next.Port != running.Pool.Port)
                    await RemoveAsync(running);

            foreach (var pool in pools)
            {
                Running? current;
                lock (_running) _running.TryGetValue(pool.Key, out current);
                if (current is null) Add(pool);
                else await ReshapeAsync(current, pool);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Add(PhpPool pool)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
        try
        {
            socket.Bind(new IPEndPoint(BindAddress, pool.Port));
            socket.Listen(Backlog);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        lock (_running) _running[pool.Key] = new Running(pool, socket);
        foreach (var spec in WorkerSpecs(pool, socket, paths)) sup.Start(spec);
    }

    // Same port: keep the socket. Restart workers one at a time when their settings changed, so the rest keep serving.
    private async Task ReshapeAsync(Running current, PhpPool pool)
    {
        var changed = current.Pool with { Workers = pool.Workers } != pool;
        var specs = WorkerSpecs(pool, current.Socket, paths);
        for (var n = 1; n <= Math.Max(current.Pool.Workers, pool.Workers); n++)
        {
            var name = WorkerName(pool.Key, n);
            if (n > pool.Workers) await sup.StopAsync(name, TimeSpan.Zero);
            else if (n > current.Pool.Workers) sup.Start(specs[n - 1]);
            else if (changed)
            {
                await sup.StopAsync(name, TimeSpan.Zero);
                sup.Start(specs[n - 1]);
            }
        }
        lock (_running) _running[pool.Key] = current with { Pool = pool };
    }

    private async Task RemoveAsync(Running running)
    {
        for (var n = 1; n <= running.Pool.Workers; n++)
            await sup.StopAsync(WorkerName(running.Pool.Key, n), TimeSpan.Zero);
        running.Socket.Dispose();
        lock (_running) _running.Remove(running.Pool.Key);
    }

    private List<KeyValuePair<string, Running>> Snapshot()
    {
        lock (_running) return _running.ToList();
    }

    private static string WorkerName(string key, int n) => $"php-{key}-{n}";

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            foreach (var (_, running) in Snapshot()) await RemoveAsync(running);
        }
        finally
        {
            _gate.Release();
        }
        _gate.Dispose();
    }
}
