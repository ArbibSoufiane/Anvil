using System.Net.Sockets;
using ArbiPache.Core;
using ArbiPache.Core.Agent;
using ArbiPache.Core.Apache;
using ArbiPache.Core.Components;
using ArbiPache.Core.Modes;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Php;
using ArbiPache.Core.Preflight;
using ArbiPache.Core.Processes;
using ArbiPache.Core.Settings;
using Serilog;

namespace ArbiPache.Agent;

/// <summary>
/// The Agent's single point of change: every operation that touches configuration or processes runs here, one at a
/// time. A configuration Apache rejects is rolled back and never restarts anything. The PHP pools run with Apache:
/// they start just before it and stop just after it.
/// </summary>
/// <param name="portFree">True when nothing listens on the port; picks ports for new PHP pools.</param>
public sealed class Orchestrator(AppPaths paths, SettingsStore store, IConfigTester tester, IApacheControl apache,
    Func<AppSettings, IReadOnlyList<PreflightFinding>> preflight, ComponentCatalog catalog, ComponentInstaller installer,
    IPhpPools php, Func<int, bool> portFree, bool runningAsService = false)
{
    public const int MaxWorkers = 64;
    public const int MaxRecycleRequests = 1_000_000;

    private const string NotInstalled = "Install Apache first, on the Apache page.";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings _settings = new();
    private IReadOnlyList<PreflightFinding> _findings = [];
    private string? _apacheUnderTest;

    public event EventHandler<AgentStatus>? StatusChanged;

    public AppSettings Settings => _settings;

    /// <summary>The Apache version whose httpd.exe tests the configuration: a candidate while switching, else the active one.</summary>
    public string? ApacheVersion => _apacheUnderTest ?? _settings.ActiveApache;

    private bool ApacheRunning => apache.Status?.State is ProcessState.Starting or ProcessState.Running or ProcessState.Restarting;

    /// <summary>
    /// Loads settings, regenerates configuration (all of it after the folder was moved), runs the preflight checks
    /// and starts Apache when the settings ask for it and nothing blocks.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        apache.StatusChanged += (_, _) => RaiseStatus();
        await Locked(async () =>
        {
            paths.EnsureCreated();
            var moved = paths.RootChanged();
            _settings = store.Load();
            var findings = new List<PreflightFinding>();
            if (store.LastLoadWarning is { } warning)
                findings.Add(new PreflightFinding("settings-reset", Severity.Warning, warning, null));

            var version = InstalledApache(_settings.ActiveApache);
            var configOk = true;
            if (version is not null)
            {
                new HttpdConfBootstrap(paths).EnsureUserConf(version);
                var applied = await ApplyConfigAsync(_settings, version, PlanPhp(_settings), ct);
                configOk = applied.Ok;
                if (!configOk)
                    findings.Add(new PreflightFinding("apache-config", Severity.Blocking,
                        "Apache rejected the configuration, so it was not started.", applied.TestOutput));
            }
            if (configOk)
            {
                if (moved) Log.Information("Root moved to {Root}; configuration regenerated", paths.Root);
                paths.MarkRoot();
            }

            findings.AddRange(preflight(_settings));
            _findings = findings;
            if (version is not null && _settings.StartServicesOnLaunch && !findings.Any(f => f.Severity == Severity.Blocking))
            {
                var started = await StartApacheCoreAsync(version, ct);
                if (!started.Ok) Log.Warning("Apache did not start on launch: {Error}", started.Error);
            }
            return ApplyResult.Success();
        }, ct);
    }

    public AgentStatus Status()
    {
        var a = apache.Status;
        ServiceStatus[] services =
        [
            new(ServiceId.Apache, a?.State ?? ProcessState.Stopped, _settings.ActiveApache,
                a?.State == ProcessState.Failed ? a.LastError : null),
            PhpStatus(),
        ];
        return new AgentStatus(_settings.Mode, services, _findings, BuildInfo.Version, runningAsService);
    }

    /// <summary>Apache and PHP start and stop together: either id, or null, means the web server.</summary>
    public Task<ApplyResult> StartAsync(ServiceId? id, CancellationToken ct) => Locked(async () =>
    {
        if (id is ServiceId.Database) return NotAvailableYet(id.Value);
        if (InstalledApache(_settings.ActiveApache) is not { } version) return ApplyResult.Failure(NotInstalled);
        if (ApacheRunning) return ApplyResult.Success();

        _findings = preflight(_settings);
        if (_findings.FirstOrDefault(f => f.Severity == Severity.Blocking) is { } blocking)
            return ApplyResult.Failure(blocking.Fix is null ? blocking.Message : $"{blocking.Message} {blocking.Fix}");
        return await StartApacheCoreAsync(version, ct);
    }, ct);

    public Task<ApplyResult> StopAsync(ServiceId? id, CancellationToken ct) => Locked(async () =>
    {
        if (id is ServiceId.Database) return NotAvailableYet(id.Value);
        await apache.StopAsync(ct);
        await php.ApplyAsync([], ct);
        return ApplyResult.Success();
    }, ct);

    /// <summary>
    /// Php restarts the pools' workers one at a time (php.ini changes take effect); Apache reloads Apache;
    /// null does both.
    /// </summary>
    public Task<ApplyResult> RestartAsync(ServiceId? id, CancellationToken ct) => Locked(async () =>
    {
        if (id is ServiceId.Database) return NotAvailableYet(id.Value);
        if (InstalledApache(_settings.ActiveApache) is not { } version) return ApplyResult.Failure(NotInstalled);
        if (!ApacheRunning) return await StartApacheCoreAsync(version, ct);
        if (id is null or ServiceId.Php)
        {
            var restarted = await Guard(() => RestartPoolsAsync(ct));
            if (id is ServiceId.Php || !restarted.Ok) return restarted;
        }

        new HttpdConfBootstrap(paths).EnsureUserConf(version);
        var pools = PlanPhp(_settings);
        var applied = await ApplyConfigAsync(_settings, version, pools, ct);
        if (!applied.Ok) return Rejected(applied, "Apache kept running with the previous configuration.");
        return await Guard(async () =>
        {
            await StartPoolsAsync(pools, ct);
            await apache.GracefulRestartAsync(ct);
        }, applied);
    }, ct);

    public async Task<ConfigTestResult> TestConfigAsync(CancellationToken ct) =>
        InstalledApache(_settings.ActiveApache) is null ? new ConfigTestResult(false, NotInstalled) : await tester.TestAsync(ct);

    /// <summary>
    /// Keeps the new settings only when Apache accepts the configuration they produce; then reloads Apache, or
    /// restarts it when a port changed. The active Apache version is changed by <see cref="ActivateAsync"/> only.
    /// </summary>
    public Task<ApplyResult> SaveSettingsAsync(AppSettings s, CancellationToken ct) => Locked(async () =>
    {
        var previous = _settings;
        // Versions and pools change through their own calls, so a page holding older settings cannot undo them.
        s = s with
        {
            ActiveApache = previous.ActiveApache,
            DefaultPhp = previous.DefaultPhp,
            PhpPools = previous.PhpPools,
            PhpPoolPorts = previous.PhpPoolPorts,
        };
        if (Validate(s) is { } invalid) return ApplyResult.Failure(invalid);

        if (InstalledApache(s.ActiveApache) is not { } version)
        {
            Save(s);
            return ApplyResult.Success();
        }

        var applied = await ApplyConfigAsync(s, version, PlanPhp(s), ct);
        if (!applied.Ok) return Rejected(applied, "The settings were not saved.");
        Save(s);
        if (!ApacheRunning) return ApplyResult.Success(applied.ChangedFiles, applied.TestOutput);

        if (s.HttpPort != previous.HttpPort || s.HttpsPort != previous.HttpsPort)
            return await Guard(async () =>
            {
                await apache.StopAsync(ct);
                await apache.StartAsync(version, s.HttpPort, ct);
            }, applied);
        if (applied.ChangedFiles.Count == 0) return ApplyResult.Success();
        return await Guard(() => apache.GracefulRestartAsync(ct), applied);
    }, ct);

    public Task<IReadOnlyList<ComponentRelease>> GetAvailableAsync(ComponentKind k, CancellationToken ct) => catalog.GetAsync(k, ct);

    public IReadOnlyList<InstalledComponent> GetInstalled(ComponentKind k) => installer.Installed(k);

    /// <summary>
    /// PHP brings the CA bundle along the first time, gets its php.ini at once, and becomes the default when there is none.
    /// </summary>
    public async Task<InstalledComponent> InstallAsync(ComponentRelease r, CancellationToken ct)
    {
        if (r.Kind == ComponentKind.Php && installer.Installed(ComponentKind.CaBundle).Count == 0)
        {
            var bundle = (await catalog.GetAsync(ComponentKind.CaBundle, ct)).FirstOrDefault()
                ?? throw new InvalidOperationException("The catalog has no CA bundle, and PHP needs one to verify HTTPS.");
            await installer.InstallAsync(bundle, null, ct);
        }
        var installed = await installer.InstallAsync(r, null, ct);
        if (r.Kind != ComponentKind.Php) return installed;

        new PhpIniBootstrap(paths).EnsureIni(r.Version);
        if (_settings.DefaultPhp is null)
        {
            var made = await SetDefaultPhpAsync(r.Version, ct);
            if (!made.Ok) Log.Warning("PHP {Version} installed but not made the default: {Error}", r.Version, made.Error);
        }
        return installed;
    }

    public IReadOnlyList<PhpVersionInfo> GetPhp()
    {
        var s = _settings;
        var running = php.Running;
        var today = DateOnly.FromDateTime(DateTime.Now);
        return installer.Installed(ComponentKind.Php).Select(i =>
        {
            var pool = running.FirstOrDefault(p => SameVersion(p.Version, i.Version));
            var tuning = PhpPoolPlanner.TuningFor(s, i.Version);
            var isDefault = SameVersion(i.Version, s.DefaultPhp);
            return new PhpVersionInfo(i.Version, i.Toolset ?? "", tuning.Workers, tuning.MaxRequests, pool?.Port ?? 0,
                isDefault ? ["htdocs"] : [], pool is null ? ProcessState.Stopped : php.StateOf(pool.Key), isDefault,
                PhpSupport.IsEndOfLife(i.Version, today));
        }).ToList();
    }

    /// <summary>Saves the pool's size and recycling; a running pool is resized at once.</summary>
    public Task<ApplyResult> SetPhpPoolAsync(string version, PhpPoolSettings pool, CancellationToken ct) => Locked(async () =>
    {
        if (InstalledPhp(version) is null) return ApplyResult.Failure($"PHP {version} is not installed.");
        if (pool.Workers is < 1 or > MaxWorkers) return ApplyResult.Failure($"Workers must be between 1 and {MaxWorkers}.");
        if (pool.MaxRequests is < 1 or > MaxRecycleRequests)
            return ApplyResult.Failure($"Recycle after must be between 1 and {MaxRecycleRequests} requests.");

        Save(_settings with { PhpPools = new Dictionary<string, PhpPoolSettings>(_settings.PhpPools) { [version] = pool } });
        if (!php.Running.Any(p => SameVersion(p.Version, version))) return ApplyResult.Success();
        return await Guard(() => StartPoolsAsync(PlanPhp(_settings), ct));
    }, ct);

    /// <summary>htdocs moves to <paramref name="version"/>, once Apache accepts the configuration that says so.</summary>
    public Task<ApplyResult> SetDefaultPhpAsync(string version, CancellationToken ct) => Locked(async () =>
    {
        if (InstalledPhp(version) is null) return ApplyResult.Failure($"PHP {version} is not installed.");
        var previous = _settings;
        var next = previous with { DefaultPhp = version };
        try
        {
            new PhpIniBootstrap(paths).EnsureIni(version);
        }
        catch (InvalidOperationException e)
        {
            return ApplyResult.Failure(e.Message);
        }
        if (InstalledApache(next.ActiveApache) is not { } apacheVersion)
        {
            Save(next);
            return ApplyResult.Success();
        }

        var pools = PlanPhp(next);
        var applied = await ApplyConfigAsync(next, apacheVersion, pools, ct);
        if (!applied.Ok) return Rejected(applied, $"PHP {previous.DefaultPhp ?? "(none)"} stays the default.");
        Save(next);
        if (!ApacheRunning) return ApplyResult.Success(applied.ChangedFiles, applied.TestOutput);
        return await Guard(async () =>
        {
            await StartPoolsAsync(pools, ct);
            await apache.GracefulRestartAsync(ct);
        }, applied);
    }, ct);

    public IReadOnlyList<PhpExtension> GetExtensions(string version) =>
        InstalledPhp(version) is null ? [] : new PhpExtensionManager(paths).List(version);

    /// <summary>Edits php.ini, then restarts that version's pool only.</summary>
    public Task<ApplyResult> SetExtensionAsync(string version, string name, bool enabled, CancellationToken ct) => Locked(async () =>
    {
        if (InstalledPhp(version) is null) return ApplyResult.Failure($"PHP {version} is not installed.");
        try
        {
            new PhpIniBootstrap(paths).EnsureIni(version);
            new PhpExtensionManager(paths).Set(version, name, enabled);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            return ApplyResult.Failure(e.Message);
        }
        if (php.Running.FirstOrDefault(p => SameVersion(p.Version, version)) is { } pool) await php.RestartAsync(pool.Key, ct);
        return ApplyResult.Success([Path.Combine(paths.PhpConfigDir(version), "php.ini")]);
    }, ct);

    /// <summary>Switches the active Apache version. The new one must pass the config test; a running Apache restarts on it.</summary>
    public Task<ApplyResult> ActivateAsync(ComponentKind k, string version, CancellationToken ct) => Locked(async () =>
    {
        if (k != ComponentKind.Apache) return ApplyResult.Failure($"Switching {k} is not available yet.");
        if (InstalledApache(version) is null) return ApplyResult.Failure($"Apache {version} is not installed.");

        var previous = _settings;
        var next = previous with { ActiveApache = version };
        var bootstrap = new HttpdConfBootstrap(paths);
        bootstrap.EnsureUserConf(version);
        var applied = await ApplyConfigAsync(next, version, PlanPhp(next), ct, alwaysTest: true);
        if (!applied.Ok)
        {
            if (InstalledApache(previous.ActiveApache) is { } old) bootstrap.EnsureUserConf(old);
            return Rejected(applied, $"Apache {previous.ActiveApache ?? "(none)"} stays active.");
        }

        Save(next);
        if (!ApacheRunning) return ApplyResult.Success(applied.ChangedFiles, applied.TestOutput);
        return await Guard(async () =>
        {
            await apache.StopAsync(ct);
            await apache.StartAsync(version, next.HttpPort, ct);
        }, applied);
    }, ct);

    /// <summary>Stops every service gracefully; used when the Agent shuts down.</summary>
    public Task StopAllAsync(CancellationToken ct) => StopAsync(null, ct);

    private async Task<ApplyResult> StartApacheCoreAsync(string version, CancellationToken ct)
    {
        new HttpdConfBootstrap(paths).EnsureUserConf(version);
        var pools = PlanPhp(_settings);
        var applied = await ApplyConfigAsync(_settings, version, pools, ct);
        if (!applied.Ok) return Rejected(applied, "Apache was not started.");
        return await Guard(async () =>
        {
            await StartPoolsAsync(pools, ct);
            await apache.StartAsync(version, _settings.HttpPort, ct);
        }, applied);
    }

    // htdocs needs the default version; sites add theirs in Phase 3.
    private IReadOnlyList<PhpPool> PlanPhp(AppSettings s) =>
        new PhpPoolPlanner(paths).Plan(s, s.DefaultPhp is { } d ? [d] : [], installer.Installed(ComponentKind.Php),
            port => php.Owns(port) || portFree(port));

    /// <summary>Re-points each php.ini, runs exactly <paramref name="pools"/>, and remembers their ports.</summary>
    private async Task StartPoolsAsync(IReadOnlyList<PhpPool> pools, CancellationToken ct)
    {
        var ini = new PhpIniBootstrap(paths);
        foreach (var pool in pools) ini.EnsureIni(pool.Version);
        try
        {
            await php.ApplyAsync(pools, ct);
        }
        catch (SocketException e)
        {
            throw new InvalidOperationException($"A PHP pool could not listen on its port: {e.Message}", e);
        }
        var ports = PhpPoolPlanner.Allocations(pools);
        if (ports.Count != _settings.PhpPoolPorts.Count || ports.Any(p => !_settings.PhpPoolPorts.TryGetValue(p.Key, out var port) || port != p.Value))
            Save(_settings with { PhpPoolPorts = ports });
    }

    private async Task RestartPoolsAsync(CancellationToken ct)
    {
        var ini = new PhpIniBootstrap(paths);
        foreach (var pool in php.Running)
        {
            ini.EnsureIni(pool.Version);
            await php.RestartAsync(pool.Key, ct);
        }
    }

    private ServiceStatus PhpStatus()
    {
        var running = php.Running;
        var states = running.Select(p => php.StateOf(p.Key)).ToList();
        var state = states.Count == 0 ? ProcessState.Stopped
            : states.All(s => s == ProcessState.Running) ? ProcessState.Running
            : states.Contains(ProcessState.Failed) ? ProcessState.Failed
            : states.First(s => s != ProcessState.Running);
        var detail = state == ProcessState.Failed
            ? @"A PHP pool stopped restarting its workers. See data\logs\php."
            : string.Join(" · ", running.Select(p => $"{PhpSupport.Minor(p.Version)} ×{p.Workers}"));
        return new ServiceStatus(ServiceId.Php, state, _settings.DefaultPhp, detail.Length == 0 ? null : detail);
    }

    private async Task<ApplyOutcome> ApplyConfigAsync(AppSettings s, string version, IReadOnlyList<PhpPool> pools, CancellationToken ct,
        bool alwaysTest = false)
    {
        _apacheUnderTest = version;
        try
        {
            var input = new RenderInput
            {
                Settings = s,
                Stock = StockConfScanner.Scan(await File.ReadAllTextAsync(paths.HttpdConf, ct)),
                Profile = BuiltInProfiles.For(s.Mode),
                ApacheVersion = version,
                Pools = pools,
            };
            var outcome = await new ConfigApplier(new ConfigRenderer(paths), new ConfigWriter(paths), tester).ApplyAsync(input, ct);
            if (!alwaysTest || outcome.ChangedFiles.Count > 0) return outcome;
            var test = await tester.TestAsync(ct);
            return outcome with { Ok = test.Ok, TestOutput = test.Output };
        }
        finally
        {
            _apacheUnderTest = null;
        }
    }

    private static async Task<ApplyResult> Guard(Func<Task> action, ApplyOutcome? applied = null)
    {
        try
        {
            await action();
            return ApplyResult.Success(applied?.ChangedFiles, applied?.TestOutput);
        }
        catch (InvalidOperationException e)
        {
            return ApplyResult.Failure(e.Message, applied?.TestOutput, applied?.ChangedFiles);
        }
    }

    private static ApplyResult Rejected(ApplyOutcome applied, string consequence) =>
        ApplyResult.Failure($"Apache rejected the configuration. {consequence}", applied.TestOutput, applied.ChangedFiles);

    private static ApplyResult NotAvailableYet(ServiceId id) => ApplyResult.Failure($"{id} is not available yet.");

    private static string? Validate(AppSettings s)
    {
        foreach (var (port, label) in new[] { (s.HttpPort, "HTTP"), (s.HttpsPort, "HTTPS"), (s.DbPort, "Database") })
            if (port is < 1 or > 65535) return $"The {label} port must be between 1 and 65535.";
        if (s.Apache.ThreadsPerChild is < 1 or > 1920) return "Threads per child must be between 1 and 1920.";
        return null;
    }

    private InstalledComponent? InstalledPhp(string version) =>
        installer.Installed(ComponentKind.Php).FirstOrDefault(i => SameVersion(i.Version, version));

    private static bool SameVersion(string a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private string? InstalledApache(string? version) =>
        version is not null && File.Exists(Path.Combine(paths.ApacheRuntime(version), "bin", "httpd.exe")) ? version : null;

    private void Save(AppSettings s)
    {
        store.Save(s);
        _settings = s;
    }

    private async Task<ApplyResult> Locked(Func<Task<ApplyResult>> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await work();
        }
        finally
        {
            _gate.Release();
            RaiseStatus();
        }
    }

    private void RaiseStatus() => StatusChanged?.Invoke(this, Status());
}
