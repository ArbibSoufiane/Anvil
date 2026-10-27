using ArbiPache.Core.Agent;
using ArbiPache.Core.Apache;
using ArbiPache.Core.Components;
using ArbiPache.Core.Database;
using ArbiPache.Core.IO;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Php;
using ArbiPache.Core.Platform;
using ArbiPache.Core.Preflight;
using ArbiPache.Core.Processes;
using ArbiPache.Core.Settings;
using Serilog;

namespace ArbiPache.Agent;

/// <summary>Wires the Agent together and runs it until cancelled or asked to shut down over the pipe.</summary>
public sealed class AgentHost(AgentOptions options, AppPaths paths)
{
    public const int ExitOk = 0;
    public const int ExitAlreadyRunning = 3;

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
        var controlUsers = store.Load().ControlUserSids;
        PrivateAcl.ControlUsers = controlUsers;

        Orchestrator? orchestrator = null;
        var tester = new ApacheConfigTester(paths, () => orchestrator?.ApacheVersion);
        var preflight = new Preflight(ports, Preflight.QueryHttpServiceState, new VcRedistCheck(new WindowsRegistryReader()), paths);
        var installer = new ComponentInstaller(paths, new Downloader(downloadHttp),
            k => k == ComponentKind.Apache ? orchestrator?.Settings.ActiveApache : null);
        orchestrator = new Orchestrator(paths, store, tester, new ApacheControl(supervisor, paths, tester, probeHttp), preflight.Run,
            new ComponentCatalog([new PhpCatalogProvider(probeHttp), new MariaDbCatalogProvider(probeHttp)], new ShippedCatalogProvider(paths)), installer,
            pools, port => ports.HolderOf(port) is null, options.RunningAsService, db: new DatabaseServer(paths, supervisor, TimeProvider.System));

        var shutdown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new AgentApi(orchestrator, stopServices => shutdown.TrySetResult(stopServices));
        bool stopServices;
        await using (var server = new PipeServer(options.PipeName, api, controlUsers))
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

            using var stopRenewal = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var renewal = new RenewalWorker(orchestrator.RenewCertificatesAsync, paths, TimeProvider.System).RunAsync(stopRenewal.Token);
            stopServices = await WaitForShutdownAsync(shutdown.Task, ct);
            await stopRenewal.CancelAsync();
            await renewal;
        }

        // Without a graceful stop, the job still ends every child when it closes below.
        if (stopServices) await orchestrator.StopAllAsync(CancellationToken.None);
        // The rest ends with the Agent's job; a killed database would need crash recovery on its next start.
        else await orchestrator.StopAsync(ServiceId.Database, CancellationToken.None);
        Log.Information("Agent stopped");
        return ExitOk;
    }

    /// <summary>Cancellation (Windows stopping the service, or the host) counts as a shutdown that stops services.</summary>
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
