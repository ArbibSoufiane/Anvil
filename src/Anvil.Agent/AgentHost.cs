using System.Net.NetworkInformation;
using Anvil.Core.Agent;
using Anvil.Core.Apache;
using Anvil.Core.Components;
using Anvil.Core.Database;
using Anvil.Core.Elevation;
using Anvil.Core.IO;
using Anvil.Core.Network;
using Anvil.Core.Paths;
using Anvil.Core.Php;
using Anvil.Core.Platform;
using Anvil.Core.Preflight;
using Anvil.Core.Processes;
using Anvil.Core.Settings;
using Serilog;

namespace Anvil.Agent;

/// <summary>Wires the Agent together and runs it until cancelled or asked to shut down over the pipe.</summary>
public sealed class AgentHost(AgentOptions options, AppPaths paths)
{
    public const int ExitOk = 0;
    public const int ExitAlreadyRunning = 3;

    private static readonly TimeSpan AddressSettleTime = TimeSpan.FromSeconds(2);

    // A fresh php-cgi worker's first request loads its extensions; give it time before calling the update broken.
    private static readonly TimeSpan PhpProbeTimeout = TimeSpan.FromSeconds(10);

    public async Task<int> RunAsync(CancellationToken ct)
    {
        using var mutex = TryClaim(options.MutexName);
        if (mutex is null)
        {
            Log.Information("Another Agent holds {Mutex}; exiting", options.MutexName);
            return ExitAlreadyRunning;
        }

        paths.EnsureCreated();
        using var job = new JobObject();
        await using var supervisor = new ProcessSupervisor(job, TimeProvider.System);
        await using var pools = new PhpPoolRunner(supervisor, paths);
        var ports = new PortProbe();
        using var probeHttp = new HttpClient();
        using var downloadHttp = new HttpClient();
        var store = new SettingsStore(paths);
        await using var names = new DnsResponder();

        Orchestrator? orchestrator = null;
        var tester = new ApacheConfigTester(paths, () => orchestrator?.ApacheVersion);
        var preflight = new Preflight(ports, Preflight.QueryHttpServiceState, new VcRedistCheck(new WindowsRegistryReader()), paths);
        var installer = new ComponentInstaller(paths, new Downloader(downloadHttp),
            k => k == ComponentKind.Apache ? orchestrator?.Settings.ActiveApache : null);
        orchestrator = new Orchestrator(paths, store, tester, new ApacheControl(supervisor, paths, tester, probeHttp, ports), preflight.Run,
            new ComponentCatalog([new PhpCatalogProvider(probeHttp)], new ShippedCatalogProvider(paths)), installer,
            pools, port => ports.HolderOf(port) is null,
            linkedDbs: new LinkedDbHost(paths, supervisor, TimeProvider.System),
            names: names, lanAddresses: LanAddresses.Find, publicAdapters: NetworkCategory.PublicAdapterIds, firewallRules: WindowsFirewallReader.Read,
            phpProbe: (port, ct) => FastCgiProbe.ProbeAsync(port, Path.Combine(paths.Run, FastCgiProbe.ScriptName), PhpProbeTimeout, ct));

        var shutdown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new AgentApi(orchestrator, stopServices => shutdown.TrySetResult(stopServices));
        bool stopServices;
        await using (var server = new PipeServer(options.PipeName, api))
        {
            server.Start();
            Log.Information("Agent {Version} listening on pipe {Pipe}", Core.BuildInfo.Version, options.PipeName);
            try
            {
                await orchestrator.InitializeAsync(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.Error(e, "Agent initialization failed");
            }

            using var stopJanitor = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var janitor = new Janitor(paths, TimeProvider.System).RunAsync(stopJanitor.Token);
            // One change of address raises several events (each adapter, each address): refresh once they settle.
            using var settle = new Timer(_ => _ = RefreshNetworkAsync(orchestrator, ct));
            NetworkAddressChangedEventHandler addressChanged = (_, _) => settle.Change(AddressSettleTime, Timeout.InfiniteTimeSpan);
            NetworkChange.NetworkAddressChanged += addressChanged;
            try
            {
                stopServices = await WaitForShutdownAsync(shutdown.Task, ct);
            }
            finally
            {
                NetworkChange.NetworkAddressChanged -= addressChanged;
            }
            await stopJanitor.CancelAsync();
            await janitor;
        }

        // Without a graceful stop, the job still ends every child when it closes below.
        if (stopServices) await orchestrator.StopAllAsync(CancellationToken.None);
        Log.Information("Agent stopped");
        return ExitOk;
    }

    private static async Task RefreshNetworkAsync(Orchestrator orchestrator, CancellationToken ct)
    {
        try
        {
            await orchestrator.RefreshNetworkAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "The network could not be checked again after an address change");
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Cancellation counts as a shutdown that stops services.</summary>
    private static async Task<bool> WaitForShutdownAsync(Task<bool> requested, CancellationToken ct)
    {
        try
        {
            return await requested.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    // Existence, not ownership, marks a running Agent: the handle stays open for the Agent's lifetime.
    // Access denied means the Agent runs under another account (the service) and also counts as running.
    private static Mutex? TryClaim(string name)
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: false, name, out var createdNew);
            if (createdNew) return mutex;
            mutex.Dispose();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
