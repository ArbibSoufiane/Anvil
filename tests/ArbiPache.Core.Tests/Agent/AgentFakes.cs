using ArbiPache.Core.Apache;
using ArbiPache.Core.Components;
using ArbiPache.Core.Database;
using ArbiPache.Core.IO;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Php;
using ArbiPache.Core.Processes;

namespace ArbiPache.Core.Tests.Agent;

internal sealed class FakePools(List<string> events) : IPhpPools
{
    public IReadOnlyList<PhpPool> Running { get; private set; } = [];
    public List<string> Restarted { get; } = [];

    public bool Owns(int port) => Running.Any(p => p.Port == port);

    public ProcessState StateOf(string key) => Running.Any(p => p.Key == key) ? ProcessState.Running : ProcessState.Stopped;

    public Task ApplyAsync(IReadOnlyList<PhpPool> pools, CancellationToken ct)
    {
        events.Add(string.Join(" ", pools.Select(p => p.Key).Prepend("pools")));
        Running = pools;
        return Task.CompletedTask;
    }

    public Task RestartAsync(string key, CancellationToken ct)
    {
        Restarted.Add(key);
        return Task.CompletedTask;
    }
}

internal sealed class FakeTester : IConfigTester
{
    public ConfigTestResult Next { get; set; } = new(true, "Syntax OK");
    public Task<ConfigTestResult> TestAsync(CancellationToken ct) => Task.FromResult(Next);
}

internal sealed class FakeApache(List<string> events) : IApacheControl
{
    public ProcessStatus? Status { get; private set; }
    public (string Version, int Port)? StartedWith { get; private set; }
    public int Restarts { get; private set; }
    public int Stops { get; private set; }

    public event EventHandler<ProcessStatus>? StatusChanged;

    public Task StartAsync(string version, int httpPort, CancellationToken ct)
    {
        StartedWith = (version, httpPort);
        events.Add("apache start");
        Set(ProcessState.Running);
        return Task.CompletedTask;
    }

    public Task GracefulRestartAsync(CancellationToken ct)
    {
        Restarts++;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        Stops++;
        events.Add("apache stop");
        Set(ProcessState.Stopped);
        return Task.CompletedTask;
    }

    private void Set(ProcessState state)
    {
        Status = new ProcessStatus(ApacheControl.ProcessName, state, state == ProcessState.Running ? 42 : null, null, 0, null);
        StatusChanged?.Invoke(this, Status);
    }
}

internal static class TestPhp
{
    // A PHP build as the installer leaves it: php-cgi, the ini template and the extension DLLs, plus its registry entry.
    public static void Install(AppPaths paths, string version, string toolset = "vs17")
    {
        var runtime = paths.PhpRuntime(version);
        Directory.CreateDirectory(Path.Combine(runtime, "ext"));
        File.WriteAllText(Path.Combine(runtime, "php-cgi.exe"), "");
        File.WriteAllText(Path.Combine(runtime, "php.ini-development"), "[PHP]\r\n;extension=intl\r\n");
        foreach (var name in PhpIniBootstrap.DefaultExtensions) File.WriteAllText(Path.Combine(runtime, "ext", $"php_{name}.dll"), "");
        var ca = Path.Combine(paths.Runtime, "cabundle", "2026-10-20");
        Directory.CreateDirectory(ca);
        File.WriteAllText(Path.Combine(ca, "cacert.pem"), "-----BEGIN CERTIFICATE-----");

        var registry = Path.Combine(paths.Runtime, "installed.json");
        var all = File.Exists(registry) ? JsonFiles.Read<List<InstalledComponent>>(registry)! : [];
        all.Add(new InstalledComponent(ComponentKind.Php, version, runtime, new string('a', 64), DateTimeOffset.UtcNow, toolset));
        JsonFiles.Write(registry, all);
    }
}

internal sealed class FakeDb(List<string> events) : IDatabaseServer
{
    private readonly Dictionary<DbEngine, ProcessState> _state = [];

    public event EventHandler<ProcessStatus>? StatusChanged { add { } remove { } }

    public HashSet<DbEngine> Existing { get; } = [];
    public string? AcceptedPassword { get; set; }
    public string? FailNextStart { get; set; }

    public ProcessStatus? Status(DbEngine e) =>
        _state.TryGetValue(e, out var s) ? new ProcessStatus(DatabaseManager.ProcessName(e), s, null, null, 0, null) : null;

    public bool Initialized(DbEngine e) => Existing.Contains(e);

    public Task InitializeAsync(DbEngine e, string version, string rootPassword, CancellationToken ct)
    {
        events.Add($"db init {e} {version}");
        Existing.Add(e);
        AcceptedPassword = rootPassword;
        return Task.CompletedTask;
    }

    public Task StartAsync(DbEngine e, string version, CancellationToken ct)
    {
        events.Add($"db start {e} {version}");
        if (FailNextStart is { } error)
        {
            FailNextStart = null;
            _state[e] = ProcessState.Failed;
            throw new InvalidOperationException(error);
        }
        _state[e] = ProcessState.Running;
        return Task.CompletedTask;
    }

    public Task StopAsync(DbEngine e, string version, CancellationToken ct)
    {
        events.Add($"db stop {e} {version}");
        _state[e] = ProcessState.Stopped;
        return Task.CompletedTask;
    }

    public Task<bool> AdoptRootPasswordAsync(DbEngine e, string password, CancellationToken ct)
    {
        events.Add($"db adopt {e}");
        return Task.FromResult(password == AcceptedPassword);
    }

    public Task SetRootPasswordAsync(DbEngine e, string password, CancellationToken ct)
    {
        AcceptedPassword = password;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, string>> DefaultsAsync(DbEngine e, string version, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["max_connections"] = "151", ["bind_address"] = "", ["character_set_server"] = "utf8mb4", ["port"] = "3306",
            ["innodb_buffer_pool_size"] = "134217728", ["slow_query_log"] = "FALSE", ["max_allowed_packet"] = "16777216",
        });

    public Task<string> BackupAsync(DbEngine e, string version, CancellationToken ct) =>
        Task.FromResult(@"D:\ArbiPache\data\backups\db\2026-10-27-120000.sql.gz");
}
