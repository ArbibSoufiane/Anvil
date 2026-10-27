using ArbiPache.Core;
using ArbiPache.Core.Agent;
using ArbiPache.Core.Apache;
using ArbiPache.Core.Components;
using ArbiPache.Core.Modes;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Preflight;
using ArbiPache.Core.Processes;
using ArbiPache.Core.Settings;
using Serilog;

namespace ArbiPache.Agent;

/// <summary>
/// The Agent's single point of change: every operation that touches configuration or processes runs here, one at a
/// time. A configuration Apache rejects is rolled back and never restarts anything.
/// </summary>
public sealed class Orchestrator(AppPaths paths, SettingsStore store, IConfigTester tester, IApacheControl apache,
    Func<AppSettings, IReadOnlyList<PreflightFinding>> preflight, ComponentCatalog catalog, ComponentInstaller installer,
    bool runningAsService = false)
{
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
                var applied = await ApplyConfigAsync(_settings, version, ct);
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
        ];
        return new AgentStatus(_settings.Mode, services, _findings, BuildInfo.Version, runningAsService);
    }

    public Task<ApplyResult> StartAsync(ServiceId? id, CancellationToken ct) => Locked(async () =>
    {
        if (id is not (null or ServiceId.Apache)) return NotAvailableYet(id.Value);
        if (InstalledApache(_settings.ActiveApache) is not { } version) return ApplyResult.Failure(NotInstalled);
        if (ApacheRunning) return ApplyResult.Success();

        _findings = preflight(_settings);
        if (_findings.FirstOrDefault(f => f.Severity == Severity.Blocking) is { } blocking)
            return ApplyResult.Failure(blocking.Fix is null ? blocking.Message : $"{blocking.Message} {blocking.Fix}");
        return await StartApacheCoreAsync(version, ct);
    }, ct);

    public Task<ApplyResult> StopAsync(ServiceId? id, CancellationToken ct) => Locked(async () =>
    {
        if (id is not (null or ServiceId.Apache)) return NotAvailableYet(id.Value);
        await apache.StopAsync(ct);
        return ApplyResult.Success();
    }, ct);

    public Task<ApplyResult> RestartAsync(ServiceId? id, CancellationToken ct) => Locked(async () =>
    {
        if (id is not (null or ServiceId.Apache)) return NotAvailableYet(id.Value);
        if (InstalledApache(_settings.ActiveApache) is not { } version) return ApplyResult.Failure(NotInstalled);
        if (!ApacheRunning) return await StartApacheCoreAsync(version, ct);

        new HttpdConfBootstrap(paths).EnsureUserConf(version);
        var applied = await ApplyConfigAsync(_settings, version, ct);
        if (!applied.Ok) return Rejected(applied, "Apache kept running with the previous configuration.");
        return await Guard(() => apache.GracefulRestartAsync(ct), applied);
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
        s = s with { ActiveApache = previous.ActiveApache };
        if (Validate(s) is { } invalid) return ApplyResult.Failure(invalid);

        if (InstalledApache(s.ActiveApache) is not { } version)
        {
            Save(s);
            return ApplyResult.Success();
        }

        var applied = await ApplyConfigAsync(s, version, ct);
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

    public Task<InstalledComponent> InstallAsync(ComponentRelease r, CancellationToken ct) => installer.InstallAsync(r, null, ct);

    /// <summary>Switches the active Apache version. The new one must pass the config test; a running Apache restarts on it.</summary>
    public Task<ApplyResult> ActivateAsync(ComponentKind k, string version, CancellationToken ct) => Locked(async () =>
    {
        if (k != ComponentKind.Apache) return ApplyResult.Failure($"Switching {k} is not available yet.");
        if (InstalledApache(version) is null) return ApplyResult.Failure($"Apache {version} is not installed.");

        var previous = _settings;
        var next = previous with { ActiveApache = version };
        var bootstrap = new HttpdConfBootstrap(paths);
        bootstrap.EnsureUserConf(version);
        var applied = await ApplyConfigAsync(next, version, ct, alwaysTest: true);
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
        var applied = await ApplyConfigAsync(_settings, version, ct);
        if (!applied.Ok) return Rejected(applied, "Apache was not started.");
        return await Guard(() => apache.StartAsync(version, _settings.HttpPort, ct), applied);
    }

    private async Task<ApplyOutcome> ApplyConfigAsync(AppSettings s, string version, CancellationToken ct, bool alwaysTest = false)
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

    private static async Task<ApplyResult> Guard(Func<Task> action, ApplyOutcome applied)
    {
        try
        {
            await action();
            return ApplyResult.Success(applied.ChangedFiles, applied.TestOutput);
        }
        catch (InvalidOperationException e)
        {
            return ApplyResult.Failure(e.Message, applied.TestOutput, applied.ChangedFiles);
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
