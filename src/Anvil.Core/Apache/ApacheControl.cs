using System.Diagnostics;
using Anvil.Core.Paths;
using Anvil.Core.Preflight;
using Anvil.Core.Processes;

namespace Anvil.Core.Apache;

/// <summary>Runs httpd under the supervisor. Every start and restart is preceded by a config test.</summary>
public sealed class ApacheControl : IApacheControl
{
    public const string ProcessName = "apache";
    internal static readonly TimeSpan ServeWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Probe = TimeSpan.FromSeconds(3);

    private readonly ProcessSupervisor sup;
    private readonly AppPaths paths;
    private readonly IConfigTester tester;
    private readonly HttpClient http;
    private readonly IPortProbe? ports;
    private string? _version;
    private int _port;

    /// <param name="ports">Tells this httpd from another program answering on its port; without it any answer counts.</param>
    public ApacheControl(ProcessSupervisor sup, AppPaths paths, IConfigTester tester, HttpClient http, IPortProbe? ports = null)
    {
        this.sup = sup;
        this.paths = paths;
        this.tester = tester;
        this.http = http;
        this.ports = ports;
        sup.Changed += (_, s) =>
        {
            if (s.Name == ProcessName) StatusChanged?.Invoke(this, s);
        };
    }

    public ProcessStatus? Status => sup.Snapshot().FirstOrDefault(s => s.Name == ProcessName);

    public event EventHandler<ProcessStatus>? StatusChanged;

    public async Task StartAsync(string version, int httpPort, CancellationToken ct)
    {
        QuietRotatelogs.Ensure(paths.ApacheRuntime(version));
        await RequireValidConfigAsync(ct);
        _version = version;
        _port = httpPort;
        sup.Start(Spec(version));
        if (await WaitServingAsync(httpPort, ct)) return;

        var status = sup.Snapshot().FirstOrDefault(s => s.Name == ProcessName);
        await sup.StopAsync(ProcessName, TimeSpan.Zero);
        if (Stranger(httpPort) is { } other)
            throw new InvalidOperationException($"Port {httpPort} is used by {other.ProcessName} (PID {other.Pid}), so Apache cannot listen on it.");
        throw new InvalidOperationException(
            $"Apache did not answer on port {httpPort} within {ServeWait.TotalSeconds:0} s. {status?.LastError}".Trim());
    }

    /// <summary>
    /// Reloads gracefully: running requests finish on the old child while a new child starts. When httpd cannot be
    /// signalled or is not serving again within 15 s, it is stopped and started instead.
    /// </summary>
    public async Task GracefulRestartAsync(CancellationToken ct)
    {
        var version = _version ?? throw new InvalidOperationException("Apache is not running.");
        await RequireValidConfigAsync(ct);
        if (Signal("restart") && await WaitServingAsync(_port, ct)) return;

        await StopAsync(ct);
        await StartAsync(version, _port, ct);
    }

    /// <summary>Asks httpd to shut down, and kills it when it is still running after 10 s.</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        if (_version is null) return;
        await sup.StopAsync(ProcessName, StopGrace, () => Task.FromResult(Signal("shutdown")));
    }

    /// <summary>True when anything answers HTTP on the port within 3 s, whatever the status code.</summary>
    public async Task<bool> IsHealthyAsync(int httpPort, CancellationToken ct)
    {
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probe.CancelAfter(Probe);
        try
        {
            using var response = await http.GetAsync($"http://127.0.0.1:{httpPort}/", HttpCompletionOption.ResponseHeadersRead, probe.Token);
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private ProcessSpec Spec(string version)
    {
        var root = paths.ApacheRuntime(version);
        return new ProcessSpec(ProcessName, Path.Combine(root, "bin", "httpd.exe"),
            ["-d", AppPaths.ForApache(root), "-f", AppPaths.ForApache(paths.HttpdConf)],
            new Dictionary<string, string>(), root, Path.Combine(paths.Logs, "apache", "console.log"));
    }

    private async Task RequireValidConfigAsync(CancellationToken ct)
    {
        var test = await tester.TestAsync(ct);
        if (!test.Ok) throw new InvalidOperationException($"Apache configuration test failed:\n{test.Output}");
    }

    private async Task<bool> WaitServingAsync(int port, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < ServeWait)
        {
            if (await IsHealthyAsync(port, ct) && Stranger(port) is null) return true;
            if (sup.Snapshot().FirstOrDefault(s => s.Name == ProcessName)?.State == ProcessState.Failed) return false;
            await Task.Delay(200, ct);
        }
        return false;
    }

    /// <summary>The program other than this Anvil's httpd listening on the port, if any.</summary>
    private PortHolder? Stranger(int port) => ports?.HolderOf(port) is { } holder && !holder.IsApacheOf(paths) ? holder : null;

    // "httpd -k restart|shutdown" only reaches an installed Windows service. A console httpd (how Anvil runs it)
    // waits on the named events ap<pid>_restart and ap<pid>_shutdown instead, in its session's namespace.
    private bool Signal(string verb)
    {
        var pid = sup.Snapshot().FirstOrDefault(s => s.Name == ProcessName && s.State == ProcessState.Running)?.Pid;
        if (pid is null) return false;
        try
        {
            using var signal = EventWaitHandle.OpenExisting($"ap{pid}_{verb}");
            return signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
