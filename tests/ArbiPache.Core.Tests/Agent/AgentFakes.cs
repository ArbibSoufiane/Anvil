using ArbiPache.Core.Apache;
using ArbiPache.Core.Components;
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
