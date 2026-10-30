using System.Net;
using Anvil.Core.Apache;
using Anvil.Core.Components;
using Anvil.Core.Database;
using Anvil.Core.IO;
using Anvil.Core.Network;
using Anvil.Core.Paths;
using Anvil.Core.Php;
using Anvil.Core.Processes;

namespace Anvil.Core.Tests.Agent;

internal sealed class FakePools(List<string> events) : IPhpPools
{
    public IReadOnlyList<PhpPool> Running { get; private set; } = [];
    public List<string> Restarted { get; } = [];

    /// <summary>PHP versions whose workers exit at once.</summary>
    public HashSet<string> Broken { get; } = [];

    public bool Owns(int port) => Running.Any(p => p.Port == port);

    public ProcessState StateOf(string key) => Running.Any(p => p.Key == key) ? ProcessState.Running : ProcessState.Stopped;

    public Task ApplyAsync(IReadOnlyList<PhpPool> pools, CancellationToken ct)
    {
        events.Add(string.Join(" ", pools.Select(p => p.Key).Prepend("pools")));
        if (pools.FirstOrDefault(p => Broken.Contains(p.Version)) is { } broken)
            throw new InvalidOperationException($"php-cgi {broken.Version} exited with code -1073741515.");
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

    /// <summary>Versions that start but never answer, as ApacheControl reports them.</summary>
    public HashSet<string> Unhealthy { get; } = [];

    /// <summary>Ports another program took after the preflight checked them.</summary>
    public HashSet<int> BusyPorts { get; } = [];

    public event EventHandler<ProcessStatus>? StatusChanged;

    public Task StartAsync(string version, int httpPort, CancellationToken ct)
    {
        events.Add("apache start");
        if (Unhealthy.Contains(version) || BusyPorts.Contains(httpPort))
        {
            Set(ProcessState.Failed);
            throw new InvalidOperationException($"Apache did not answer on port {httpPort} within 15 s.");
        }
        StartedWith = (version, httpPort);
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

    /// <summary>Apache crashed too often, so the supervisor stopped restarting it.</summary>
    public void GiveUp() => Set(ProcessState.Failed);

    private void Set(ProcessState state)
    {
        Status = new ProcessStatus(ApacheControl.ProcessName, state, state == ProcessState.Running ? 42 : null, null, 0, null);
        StatusChanged?.Invoke(this, Status);
    }
}

internal static class TestPhp
{
    // A PHP build as the installer leaves it: php-cgi, the ini template and the extension DLLs, plus its registry entry.
    public static void Install(AppPaths paths, string version, string toolset = "vs17", bool threadSafe = false)
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
        all.Add(new InstalledComponent(ComponentKind.Php, version, runtime, new string('a', 64), DateTimeOffset.UtcNow, toolset, ThreadSafe: threadSafe));
        JsonFiles.Write(registry, all);
    }
}

/// <summary>Records each ApplyAsync; FailOn is an address another program holds port 53 on.</summary>
internal sealed class FakeNameService : INameService
{
    public (string? Domain, IReadOnlyList<IPAddress> Addresses) LastCall { get; private set; } = (null, []);
    public IPAddress? FailOn { get; set; }
    public IReadOnlyList<IPAddress> Listening { get; private set; } = [];

    public Task<IReadOnlyList<DnsBindFailure>> ApplyAsync(string? networkDomain, IReadOnlyList<IPAddress> addresses, CancellationToken ct)
    {
        LastCall = (networkDomain, addresses);
        IReadOnlyList<IPAddress> wanted = networkDomain is null ? [] : addresses;
        Listening = wanted.Where(a => !a.Equals(FailOn)).ToList();
        return Task.FromResult<IReadOnlyList<DnsBindFailure>>(wanted.Where(a => a.Equals(FailOn)).Select(a => new DnsBindFailure(a, "in use")).ToList());
    }
}
