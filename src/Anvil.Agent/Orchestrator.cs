using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Anvil.Core;
using Anvil.Core.Agent;
using Anvil.Core.Apache;
using Anvil.Core.Certificates;
using Anvil.Core.Components;
using Anvil.Core.Database;
using Anvil.Core.Elevation;
using Anvil.Core.Hosts;
using Anvil.Core.IO;
using Anvil.Core.Network;
using Anvil.Core.Paths;
using Anvil.Core.Php;
using Anvil.Core.Preflight;
using Anvil.Core.Processes;
using Anvil.Core.Settings;
using Anvil.Core.Sites;
using Serilog;

namespace Anvil.Agent;

/// <summary>
/// The Agent's single point of change: every operation that touches configuration or processes runs here, one at a
/// time. A configuration Apache rejects is rolled back and never restarts anything. The PHP pools run with Apache:
/// they start just before it and stop just after it.
/// </summary>
/// <param name="portFree">True when nothing listens on the port; picks ports for new PHP pools.</param>
/// <param name="hostsText">Reads the Windows hosts file; the Agent never writes it (the Elevator does).</param>
/// <param name="names">Answers the network domain while Apache runs; null runs none.</param>
/// <param name="lanAddresses">This PC's LAN addresses; null means none.</param>
/// <param name="publicAdapters">Ids of the adapters on networks Windows treats as Public; null means none.</param>
/// <param name="firewallRules">Windows Firewall's inbound rules; null means none.</param>
public sealed partial class Orchestrator(AppPaths paths, SettingsStore store, IConfigTester tester, IApacheControl apache,
    Func<AppSettings, IReadOnlyList<PreflightFinding>> preflight, ComponentCatalog catalog, ComponentInstaller installer,
    IPhpPools php, Func<int, bool> portFree, Func<string>? hostsText = null,
    string? legacyHome = null, TimeProvider? time = null,
    INameService? names = null, Func<IReadOnlyList<LanAddress>>? lanAddresses = null, Func<IReadOnlySet<string>>? publicAdapters = null,
    Func<IReadOnlyList<ExistingFirewallRule>>? firewallRules = null, Func<int, CancellationToken, Task<bool>>? phpProbe = null,
    string? userProfile = null, Func<string, string?>? phpVersionOf = null, ILinkedDbHost? linkedDbs = null)
{
    public const int MaxWorkers = 64;
    public const int MaxRecycleRequests = 1_000_000;

    private const string NotInstalled = "Install Apache first, on the Apache page.";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<string> _hostsText = hostsText ?? ReadSystemHosts;
    // Runs data\run\probe.php on the pool at a port. Without one, a pool that starts counts as answering.
    private readonly Func<int, CancellationToken, Task<bool>> _phpProbe = phpProbe ?? ((_, _) => Task.FromResult(true));
    private readonly string _legacyHome = legacyHome ?? LegacyImporter.DefaultHome();
    private AppSettings _settings = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private IReadOnlyList<PreflightFinding> _findings = [];
    private string? _apacheUnderTest;
    private readonly Func<IReadOnlyList<LanAddress>> _lanAddresses = lanAddresses ?? (() => []);
    private readonly Func<IReadOnlySet<string>> _publicAdapters = publicAdapters ?? (() => new HashSet<string>());
    private readonly Func<IReadOnlyList<ExistingFirewallRule>> _firewallRules = firewallRules ?? (() => []);
    private IReadOnlyList<PreflightFinding> _networkFindings = [];
    private IReadOnlyList<DnsBindFailure> _bindFailures = [];
    private (string? Domain, IPAddress[] Addresses)? _namesApplied;
    private bool _retryNames;

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
        apache.StatusChanged += (_, status) =>
        {
            RaiseStatus();
            if (status.State == ProcessState.Failed) _ = FollowApacheFailureAsync();
        };

        await Locked(async () =>
        {
            paths.EnsureCreated();
            var moved = paths.RootChanged();
            _settings = store.Load();
            if (moved && paths.RecordedRoot() is { } oldRoot) RebaseSavedPaths(oldRoot);
            var findings = new List<PreflightFinding>();
            if (store.LastLoadWarning is { } warning)
                findings.Add(new PreflightFinding("settings-reset", Severity.Warning, warning, null));

            ReissueExpiringLocalCertificates();
            var version = InstalledApache(_settings.ActiveApache);
            var configOk = true;
            if (version is not null)
            {
                new HttpdConfBootstrap(paths).EnsureUserConf(version);
                var applied = await ApplyConfigAsync(_settings, version, PlanPhp(_settings), ct);
                configOk = applied.Ok;
                if (!configOk)
                    findings.Add(new PreflightFinding("apache-config", Severity.Blocking,
                        applied.WriteError ?? "Apache rejected the configuration, so it was not started.",
                        applied.WriteError is null ? applied.TestOutput : null));
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

    /// <summary>
    /// After the folder was moved or copied: saved paths inside the old folder (site roots, the htdocs folder,
    /// name the same place inside this one, so a copy never serves or writes into the original.
    /// </summary>
    private void RebaseSavedPaths(string oldRoot)
    {
        Log.Information("Re-pointing saved paths from {OldRoot} to {Root}", oldRoot, paths.Root);
        if (_settings.HtdocsPath is { } htdocs && paths.Rebase(htdocs, oldRoot) is { } rebasedHtdocs)
        {
            _settings = _settings with { HtdocsPath = rebasedHtdocs };
            store.Save(_settings);
        }

        var sites = new SiteStore(paths);
        foreach (var site in sites.All())
        {
            if (paths.Rebase(site.Root, oldRoot) is { } root && root != site.Root) sites.Save(site with { Root = root });
        }
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
        return new AgentStatus(services, [.. _findings, .. _networkFindings], BuildInfo.Version,
            names?.Listening.Select(a => a.ToString()).ToList() ?? []);
    }

    /// <summary>Apache and PHP start and stop together: any id, or null, means the web server.</summary>
    public Task<ApplyResult> StartAsync(ServiceId? id, CancellationToken ct) => Locked(() => StartWebCoreAsync(ct), ct);

    private async Task<ApplyResult> StartWebCoreAsync(CancellationToken ct)
    {
        if (InstalledApache(_settings.ActiveApache) is not { } version) return ApplyResult.Failure(NotInstalled);
        if (ApacheRunning) return ApplyResult.Success();

        _findings = preflight(_settings);
        if (_findings.FirstOrDefault(f => f.Severity == Severity.Blocking) is { } blocking)
            return ApplyResult.Failure(blocking.Fix is null ? blocking.Message : $"{blocking.Message} {blocking.Fix}");
        return await StartApacheCoreAsync(version, ct);
    }

    public Task<ApplyResult> StopAsync(ServiceId? id, CancellationToken ct) => Locked(async () =>
    {
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
        var saved = await SaveSettingsCoreAsync(s, ct);
        return saved.Ok ? saved with { RequiredFirewall = FirewallRequest() } : saved;
    }, ct);

    private async Task<ApplyResult> SaveSettingsCoreAsync(AppSettings s, CancellationToken ct)
    {
        var previous = _settings;
        // Versions and pools change through their own calls, so a page holding older settings cannot undo them.
        s = s with
        {
            ActiveApache = previous.ActiveApache,
            DefaultPhp = previous.DefaultPhp,
            PhpPools = previous.PhpPools,
            PhpPoolPorts = previous.PhpPoolPorts,
            NetworkDomain = NetworkNames.NormalizeDomain(s.NetworkDomain),
        };
        if (Validate(s) is { } invalid) return ApplyResult.Failure(invalid);
        // A port another program holds would take the sites down: refuse it while they still run.
        int[] fresh = [.. new[] { s.HttpPort, s.HttpsPort }.Except([previous.HttpPort, previous.HttpsPort])];
        if (fresh.Length > 0
            && preflight(s).FirstOrDefault(f => f.Severity == Severity.Blocking && fresh.Any(p => f.Id == $"port-{p}" || f.Id == $"http-sys-{p}")) is { } busy)
            return ApplyResult.Failure($"{(busy.Fix is null ? busy.Message : $"{busy.Message} {busy.Fix}")} The settings were not saved.");

        if (InstalledApache(s.ActiveApache) is not { } version)
        {
            Save(s);
            return ApplyResult.Success();
        }

        var applied = await ApplyConfigAsync(s, version, PlanPhp(s), ct);
        if (!applied.Ok) return Rejected(applied, "The settings were not saved.");
        Save(s);
        if (!ApacheRunning) return ApplyResult.Success(applied.ChangedFiles, applied.TestOutput);

        var served = s.HttpPort != previous.HttpPort || s.HttpsPort != previous.HttpsPort
            ? await Guard(async () =>
            {
                await apache.StopAsync(ct);
                await apache.StartAsync(version, s.HttpPort, ct);
            }, applied)
            : applied.ChangedFiles.Count == 0
                ? ApplyResult.Success()
                : await Guard(() => apache.GracefulRestartAsync(ct), applied);
        return served.Ok ? served : await RollBackAsync(previous, version, served, ct);
    }

    /// <summary>Puts the previous settings and their configuration back after Apache could not serve new ones, and serves those again.</summary>
    private async Task<ApplyResult> RollBackAsync(AppSettings previous, string version, ApplyResult failed, CancellationToken ct)
    {
        Save(previous);
        var restored = await ApplyConfigAsync(previous, version, PlanPhp(previous), ct);
        var serving = !restored.Ok
            ? ApplyResult.Failure("Apache rejected the previous configuration too.")
            : await Guard(async () =>
            {
                if (ApacheRunning) await apache.GracefulRestartAsync(ct);
                else await apache.StartAsync(version, previous.HttpPort, ct);
            });
        return ApplyResult.Failure(serving.Ok
                ? $"{failed.Error} Anvil went back to the previous settings, and Apache runs as before."
                : $"{failed.Error} Anvil went back to the previous settings, but Apache did not start again: {serving.Error}",
            failed.ConfigTestOutput, failed.ChangedFiles);
    }

    public Task<IReadOnlyList<ComponentRelease>> GetAvailableAsync(ComponentKind k, CancellationToken ct) => catalog.GetAsync(k, ct);

    public IReadOnlyList<InstalledComponent> GetInstalled(ComponentKind k) => installer.Installed(k);

    /// <summary>
    /// PHP brings the CA bundle along the first time, gets its php.ini at once (a patch release copies the one of the
    /// branch's older patch), and becomes the default when there is none.
    /// </summary>
    public async Task<InstalledComponent> InstallAsync(ComponentRelease offered, CancellationToken ct)
    {
        // The window sends what it was offered; only the Agent's catalog says where a release comes from and its hash.
        var r = (await catalog.GetAsync(offered.Kind, ct)).FirstOrDefault(c => SameVersion(c.Version, offered.Version)
                    && string.Equals(c.Toolset, offered.Toolset, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(c.AppliesTo, offered.AppliesTo, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"{offered.Kind} {offered.Version} is not in Anvil's catalog.");
        if (r.Kind == ComponentKind.Php) await EnsureCaBundleAsync(ct);
        var installed = await installer.InstallAsync(r, null, ct);
        if (r.Kind == ComponentKind.Xdebug) await TurnOnXdebugAsync(installed, ct);
        if (r.Kind != ComponentKind.Php) return installed;

        ComponentUpdater.CarryPhpConfig(paths, r.Version, installer.Installed(ComponentKind.Php).Select(i => i.Version));
        PhpIni().EnsureIni(r.Version);
        if (_settings.DefaultPhp is null)
        {
            var made = await SetDefaultPhpAsync(r.Version, ct);
            if (!made.Ok) Log.Warning("PHP {Version} installed but not made the default: {Error}", r.Version, made.Error);
        }
        return installed;
    }

    private async Task EnsureCaBundleAsync(CancellationToken ct)
    {
        if (installer.Installed(ComponentKind.CaBundle).Count > 0) return;
        var bundle = (await catalog.GetAsync(ComponentKind.CaBundle, ct)).FirstOrDefault()
            ?? throw new InvalidOperationException("The catalog has no CA bundle, and PHP needs one to verify HTTPS.");
        await installer.InstallAsync(bundle, null, ct);
    }

    /// <summary>Where a PHP version's files are: its runtime folder, or the folder it was linked from.</summary>
    private string PhpFiles(string version) => InstalledPhp(version)?.Path ?? paths.PhpRuntime(version);

    private PhpIniBootstrap PhpIni() => new(paths, PhpFiles);

    // Anvil's Xdebug builds are non-thread-safe, so a thread-safe PHP (a linked one) can only load an Xdebug from its own ext.
    private PhpExtensionManager Extensions(string version) =>
        new(paths, InstalledPhp(version)?.ThreadSafe == true ? [] : installer.Installed(ComponentKind.Xdebug), PhpFiles);

    /// <summary>A new Xdebug build loads into every installed PHP of its branch; their running pools restart.</summary>
    private async Task TurnOnXdebugAsync(InstalledComponent xdebug, CancellationToken ct)
    {
        foreach (var version in installer.Installed(ComponentKind.Php).Where(i => !i.ThreadSafe).Select(i => i.Version).Where(v => PhpSupport.Minor(v) == xdebug.AppliesTo))
        {
            PhpIni().EnsureIni(version);
            Extensions(version).Set(version, "xdebug", enabled: true);
            if (php.Running.FirstOrDefault(p => SameVersion(p.Version, version)) is { } pool) await php.RestartAsync(pool.Key, ct);
        }
    }

    public IReadOnlyList<PhpVersionInfo> GetPhp()
    {
        var s = _settings;
        var running = php.Running;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var sites = new SiteStore(paths).All().Where(x => x.Enabled && x.Kind != SiteKind.Htdocs).ToList();
        return installer.Installed(ComponentKind.Php).Select(i =>
        {
            var pool = running.FirstOrDefault(p => SameVersion(p.Version, i.Version));
            var tuning = PhpPoolPlanner.TuningFor(s, i.Version);
            var isDefault = SameVersion(i.Version, s.DefaultPhp);
            var usedBy = sites.Where(x => Uses(x, i.Version, s.DefaultPhp)).Select(x => x.Name);
            return new PhpVersionInfo(i.Version, i.Toolset ?? "", tuning.Workers, tuning.MaxRequests, pool?.Port ?? 0,
                (isDefault ? usedBy.Prepend("htdocs") : usedBy).ToList(), pool is null ? ProcessState.Stopped : php.StateOf(pool.Key), isDefault,
                PhpSupport.IsEndOfLife(i.Version, today), i.Path, i.Linked, i.ThreadSafe);
        }).ToList();

        static bool Uses(SiteDefinition site, string version, string? defaultPhp) =>
            site.Type == SiteType.Php && SameVersion(version, site.PhpVersion ?? defaultPhp);
    }

    public IReadOnlyList<SiteDefinition> GetSites() => new SiteStore(paths).All();

    public IReadOnlyList<HtdocsEntry> GetHtdocs() => HtdocsOf(_settings, new SiteStore(paths).All());

    /// <summary>The site rules, plus: every PHP version the site names is installed.</summary>
    public IReadOnlyList<ValidationError> ValidateSite(SiteDefinition s)
    {
        var (site, _, others) = Prepare(s, new SiteStore(paths).All());
        return ValidateAgainst(site, others);
    }

    /// <summary>
    /// Validates, writes a local development certificate when needed, saves the site, then keeps it only if Apache
    /// accepts the configuration; a rejected change puts every file back as it was.
    /// </summary>
    public Task<ApplyResult> SaveSiteAsync(SiteDefinition s, CancellationToken ct) => Locked(async () =>
    {
        var sites = new SiteStore(paths);
        var (site, renamedFrom, others) = Prepare(s, sites.All());
        if (ValidateAgainst(site, others) is { Count: > 0 } errors) return ApplyResult.Failure(string.Join(" ", errors.Select(e => e.Message)));

        var undo = new List<Action>();
        var certs = new CertificateStore(paths);
        try
        {
            if (site.Cert == CertSource.LocalDev && NeedsLocalCertificate(certs.Get(site.Id), CertNames(site)))
            {
                RememberFolder(CertDir(site.Id), undo);
                new LocalCa(paths, certs, _time).Issue(site.Id, CertNames(site));
            }

            Remember(SiteFile(site.Id), undo);
            sites.Save(site);
            if (renamedFrom is not null)
            {
                Remember(SiteFile(renamedFrom), undo);
                sites.Delete(renamedFrom);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or CryptographicException)
        {
            Undo(undo);
            return ApplyResult.Failure($"The site was not saved: {e.Message}");
        }

        var (applied, result) = await ApplySitesAsync(undo, "The site was not saved.", ct);
        if (!applied) return result;
        if (renamedFrom is not null) certs.Delete(renamedFrom);

        return result with { HostsDomains = HostsChange(), RequiredFirewall = FirewallRequest() };
    }, ct);

    /// <summary>Removes the site with its certificate. Its logs stay.</summary>
    public Task<ApplyResult> RemoveSiteAsync(string id, CancellationToken ct) => Locked(async () =>
    {
        var sites = new SiteStore(paths);
        if (sites.All().All(x => x.Id != id)) return ApplyResult.Failure("That site no longer exists.");

        var undo = new List<Action>();
        Remember(SiteFile(id), undo);
        sites.Delete(id);
        var (applied, result) = await ApplySitesAsync(undo, "The site was not removed.", ct);
        if (!applied) return result;
        new CertificateStore(paths).Delete(id);
        return result with { HostsDomains = HostsChange() };
    }, ct);

    public Task<ApplyResult> SetSiteEnabledAsync(string id, bool enabled, CancellationToken ct) => Locked(async () =>
    {
        var sites = new SiteStore(paths);
        if (sites.All().FirstOrDefault(x => x.Id == id) is not { } site) return ApplyResult.Failure("That site no longer exists.");

        var undo = new List<Action>();
        Remember(SiteFile(id), undo);
        sites.Save(site with { Enabled = enabled });
        var (applied, result) = await ApplySitesAsync(undo, enabled ? "The site stays off." : "The site stays on.", ct);
        return applied ? result with { HostsDomains = HostsChange(), RequiredFirewall = FirewallRequest() } : result;
    }, ct);

    /// <summary>
    /// Brings in ArbiPache 3's sites, with local development certificates for its HTTPS sites. Either every importable
    /// site is kept or, when Apache rejects the result, none is.
    /// </summary>
    public Task<ImportReport> ImportLegacyAsync(CancellationToken ct) => Locked(async () =>
    {
        var sites = new SiteStore(paths);
        var installedPhp = installer.Installed(ComponentKind.Php).Select(i => i.Version).ToList();
        var report = LegacyImporter.Import(_legacyHome, sites.All(), installedPhp, out var imported);
        if (imported.Count == 0) return report with { Result = ApplyResult.Success() };

        var undo = new List<Action>();
        var certs = new CertificateStore(paths);
        try
        {
            foreach (var site in imported)
            {
                if (site.Cert == CertSource.LocalDev && NeedsLocalCertificate(certs.Get(site.Id), CertNames(site)))
                {
                    RememberFolder(CertDir(site.Id), undo);
                    new LocalCa(paths, certs, _time).Issue(site.Id, CertNames(site));
                }

                Remember(SiteFile(site.Id), undo);
                sites.Save(site);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or CryptographicException)
        {
            Undo(undo);
            return report with { Imported = [], Result = ApplyResult.Failure($"Nothing was imported: {e.Message}") };
        }

        var (applied, result) = await ApplySitesAsync(undo, "Nothing was imported.", ct);
        return applied
            ? report with { Result = result with { HostsDomains = HostsChange() } }
            : report with { Imported = [], Result = result };
    }, ct);

    /// <summary>Issues every local development certificate again; a rejected configuration puts the old ones back.</summary>
    public IReadOnlyDictionary<string, SiteCertificate> GetCertificates() => new CertificateStore(paths).All();

    /// <summary>
    /// The rules Windows Firewall needs for what is open to the network now; null when nothing is open or the rules are
    /// already in place.
    /// </summary>
    public FirewallSetRequest? FirewallRequest() => NeededFirewall() is { } needed && !FirewallReady(needed) ? needed : null;

    /// <summary>Reads this PC's addresses, its networks and the firewall again, and tries the name service on every address again.</summary>
    public Task RefreshNetworkAsync(CancellationToken ct) => Locked(() =>
    {
        _retryNames = true;
        return Task.FromResult(true);
    }, ct);

    /// <summary>
    /// Apache that crashed too often fails with no operation running, so nothing else would stop the name service
    /// pointing colleagues at it. Waits for any running operation, then refreshes as one would.
    /// </summary>
    private async Task FollowApacheFailureAsync()
    {
        try
        {
            await Locked(() => Task.FromResult(true), CancellationToken.None);
        }
        catch (Exception e)
        {
            Log.Warning(e, "The name service could not follow the web server");
        }
    }

    private FirewallSetRequest? NeededFirewall() => FirewallRulePlanner.Needed(_settings, new SiteStore(paths).All());

    private bool FirewallReady(FirewallSetRequest needed) =>
        FirewallRulePlanner.Satisfied(FirewallRulePlanner.Plan(needed, paths), _firewallRules(), paths);

    /// <summary>
    /// At startup, before the configuration is written: issues again every enabled site's local certificate that is
    /// missing, lacks a name or expires within 30 days. A certificate that cannot be issued stays as it is.
    /// </summary>
    private void ReissueExpiringLocalCertificates()
    {
        var certs = new CertificateStore(paths);
        foreach (var site in new SiteStore(paths).All().Where(s => s.Enabled && s.Cert == CertSource.LocalDev))
        {
            if (!NeedsLocalCertificate(certs.Get(site.Id), CertNames(site))) continue;
            try
            {
                new LocalCa(paths, certs, _time).Issue(site.Id, CertNames(site));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
            {
                Log.Warning(e, "The certificate for {Site} was not issued again", site.Id);
            }
        }
    }

    public Task<ApplyResult> ReissueLocalCertificatesAsync(CancellationToken ct) => Locked(async () =>
    {
        var undo = new List<Action>();
        var certs = new CertificateStore(paths);
        try
        {
            foreach (var site in new SiteStore(paths).All().Where(s => s.Cert == CertSource.LocalDev))
            {
                RememberFolder(CertDir(site.Id), undo);
                new LocalCa(paths, certs, _time).Issue(site.Id, CertNames(site));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Undo(undo);
            return ApplyResult.Failure($"The certificates were not issued again: {e.Message}");
        }

        return (await ApplySitesAsync(undo, "The old certificates stay.", ct)).Result;
    }, ct);

    /// <summary>Copies a project into htdocs\&lt;folderName&gt;. An existing folder or htdocs entry of that name is never overwritten.</summary>
    public Task<ApplyResult> CopyIntoHtdocsAsync(string sourceFolder, string folderName, CancellationToken ct) => Locked(async () =>
    {
        if (!SiteSet.IsFolderName(folderName)) return ApplyResult.Failure("Use letters, digits, dots, dashes and underscores for the folder name.");
        if (!Directory.Exists(sourceFolder)) return ApplyResult.Failure($"{sourceFolder} does not exist.");
        var htdocs = Path.GetFullPath(HtdocsDir(_settings));
        var target = Path.Combine(htdocs, folderName);
        if (Directory.Exists(target) || File.Exists(target)) return ApplyResult.Failure($"htdocs already has a folder named {folderName}.");
        if (new SiteStore(paths).All().Any(x => x.Kind == SiteKind.Htdocs && string.Equals(x.Name, folderName, StringComparison.OrdinalIgnoreCase)))
            return ApplyResult.Failure($"localhost/{folderName} is already set up.");
        if ((htdocs + Path.DirectorySeparatorChar).StartsWith(Path.GetFullPath(sourceFolder).TrimEnd('\\') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return ApplyResult.Failure("A folder cannot be copied into itself.");

        try
        {
            await Task.Run(() => CopyTree(sourceFolder, target), ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            try { Directory.Delete(target, recursive: true); } catch (IOException) { }
            return ApplyResult.Failure($"The copy did not finish: {e.Message}");
        }

        return ApplyResult.Success();
    }, ct);

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
            PhpIni().EnsureIni(version);
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
        InstalledPhp(version) is null ? [] : Extensions(version).List(version);

    /// <summary>Edits php.ini, then restarts that version's pool only.</summary>
    public Task<ApplyResult> SetExtensionAsync(string version, string name, bool enabled, CancellationToken ct) => Locked(async () =>
    {
        if (InstalledPhp(version) is null) return ApplyResult.Failure($"PHP {version} is not installed.");
        try
        {
            PhpIni().EnsureIni(version);
            Extensions(version).Set(version, name, enabled);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            return ApplyResult.Failure(e.Message);
        }
        if (php.Running.FirstOrDefault(p => SameVersion(p.Version, version)) is { } pool) await php.RestartAsync(pool.Key, ct);
        return ApplyResult.Success([Path.Combine(paths.PhpConfigDir(version), "php.ini")]);
    }, ct);

    /// <summary>
    /// Prepares php.ini first, which takes over a linked folder's extensions the first time; a running pool restarts when
    /// that changed the file.
    /// </summary>
    public Task<PhpIniValues> GetPhpIniAsync(string version, CancellationToken ct) => Locked(async () =>
    {
        if (InstalledPhp(version) is null) return new PhpIniValues(new Dictionary<string, string>(), false, false);
        var file = Path.Combine(paths.PhpConfigDir(version), "php.ini");
        var before = File.Exists(file) ? await File.ReadAllTextAsync(file, ct) : null;
        try
        {
            PhpIni().EnsureIni(version);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Log.Warning(e, "php.ini of PHP {Version} could not be prepared", version);
        }
        if (before is not null && File.Exists(file) && await File.ReadAllTextAsync(file, ct) != before
            && php.Running.FirstOrDefault(p => SameVersion(p.Version, version)) is { } pool)
            await php.RestartAsync(pool.Key, ct);
        return new PhpIniSettings(paths, PhpFiles).Read(version);
    }, ct);

    /// <summary>Writes the page's php.ini values, then restarts that version's pool only.</summary>
    public Task<ApplyResult> SavePhpIniAsync(string version, IReadOnlyDictionary<string, string> values, CancellationToken ct) =>
        EditPhpIniAsync(version, () => new PhpIniSettings(paths, PhpFiles).Write(version, values), ct);

    /// <summary>Turns OPcache on or off in php.ini, then restarts that version's pool only.</summary>
    public Task<ApplyResult> SetOpcacheAsync(string version, bool enabled, CancellationToken ct) =>
        EditPhpIniAsync(version, () => new PhpIniSettings(paths, PhpFiles).SetOpcache(version, enabled), ct);

    private Task<ApplyResult> EditPhpIniAsync(string version, Action edit, CancellationToken ct) => Locked(async () =>
    {
        if (InstalledPhp(version) is null) return ApplyResult.Failure($"PHP {version} is not installed.");
        try
        {
            PhpIni().EnsureIni(version);
            edit();
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return ApplyResult.Failure(e.Message);
        }
        if (php.Running.FirstOrDefault(p => SameVersion(p.Version, version)) is { } pool) await php.RestartAsync(pool.Key, ct);
        return ApplyResult.Success([Path.Combine(paths.PhpConfigDir(version), "php.ini")]);
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

    // htdocs needs the default version; enabled sites and htdocs folders add theirs.
    private IReadOnlyList<PhpPool> PlanPhp(AppSettings s)
    {
        var sites = new SiteStore(paths).All();
        return new PhpPoolPlanner(paths).Plan(s, SiteSet.RequiredPhpVersions(sites, HtdocsOf(s, sites), s.DefaultPhp),
            installer.Installed(ComponentKind.Php), port => php.Owns(port) || portFree(port));
    }

    /// <summary>
    /// The site as it will be saved: its id follows its kind and name (a changed name is a rename of the saved site),
    /// it keeps its creation time, and it is checked against every other site.
    /// </summary>
    private static (SiteDefinition Site, string? RenamedFrom, IReadOnlyList<SiteDefinition> Others) Prepare(
        SiteDefinition s, IReadOnlyList<SiteDefinition> all)
    {
        var id = SiteSet.IdFor(s.Kind, s.Name);
        var renamedFrom = s.Id != id && all.Any(x => x.Id == s.Id) ? s.Id : null;
        var previous = all.FirstOrDefault(x => x.Id == (renamedFrom ?? id));
        var site = s with { Id = id, Created = previous?.Created ?? (s.Created == default ? DateTimeOffset.UtcNow : s.Created) };
        return (site, renamedFrom, all.Where(x => x.Id != renamedFrom).ToList());
    }

    private IReadOnlyList<ValidationError> ValidateAgainst(SiteDefinition site, IReadOnlyList<SiteDefinition> others)
    {
        var errors = SiteValidator.Validate(site, others, paths).ToList();
        if (site.Type == SiteType.Php && site.PhpVersion is { } version && InstalledPhp(version) is null && errors.All(e => e.Field != "PhpVersion"))
            errors.Add(new ValidationError("PhpVersion", $"PHP {version} is not installed. Install it on the PHP page."));
        return errors;
    }

    /// <summary>
    /// Renders the configuration for the sites now on disk. Applied is false when Apache rejected it: then
    /// <paramref name="undo"/> has put the site files back and the result explains why.
    /// </summary>
    private async Task<(bool Applied, ApplyResult Result)> ApplySitesAsync(List<Action> undo, string consequence, CancellationToken ct)
    {
        if (InstalledApache(_settings.ActiveApache) is not { } version) return (true, ApplyResult.Success());
        var pools = PlanPhp(_settings);
        var applied = await ApplyConfigAsync(_settings, version, pools, ct);
        if (!applied.Ok)
        {
            Undo(undo);
            return (false, Rejected(applied, consequence));
        }

        if (!ApacheRunning) return (true, ApplyResult.Success(applied.ChangedFiles, applied.TestOutput));
        return (true, await Guard(async () =>
        {
            await StartPoolsAsync(pools, ct);
            await apache.GracefulRestartAsync(ct);
        }, applied));
    }

    /// <summary>
    /// The hosts entries the sites need, when they differ from Anvil's block in the hosts file now or ArbiPache 3's block
    /// still lists one of them; else null. Every name of every enabled domain site: sites are for this PC, and only the
    /// hosts file points a name here, whatever it ends in.
    /// </summary>
    private IReadOnlyList<string>? HostsChange()
    {
        var wanted = new SiteStore(paths).All()
            .Where(s => s.Enabled && s.Kind == SiteKind.Domain)
            .SelectMany(s => s.Aliases.Prepend(s.Name)
                .Select(n => SiteSet.NormalizeHost(n) ?? n))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        string current;
        try
        {
            current = _hostsText();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            current = "";
        }

        var unchanged = HostsFile.ManagedDomains(current).ToHashSet(StringComparer.Ordinal).SetEquals(wanted)
            && !HostsFile.LegacyDomains(current).Intersect(wanted, StringComparer.OrdinalIgnoreCase).Any();
        return unchanged ? null : wanted;
    }

    private static string ReadSystemHosts()
    {
        var hosts = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
        return File.Exists(hosts) ? File.ReadAllText(hosts, Encoding.Latin1) : "";
    }

    /// <summary>The domain and its aliases.</summary>
    private static IReadOnlyList<string> CertNames(SiteDefinition site) =>
        site.Aliases.Prepend(site.Name).Select(n => SiteSet.NormalizeHost(n) ?? n).Distinct(StringComparer.Ordinal).ToList();

    private static bool NeedsLocalCertificate(SiteCertificate? current, IReadOnlyList<string> names) =>
        current is null || current.Source != CertSource.LocalDev || current.NotAfter < DateTimeOffset.UtcNow.AddDays(30)
        || !names.All(n => current.Names.Contains(n, StringComparer.OrdinalIgnoreCase));

    private IReadOnlyList<HtdocsEntry> HtdocsOf(AppSettings s, IReadOnlyList<SiteDefinition> sites) =>
        SiteSet.DiscoverHtdocs(HtdocsDir(s), sites.Where(x => x.Kind == SiteKind.Htdocs).ToList());

    private string HtdocsDir(AppSettings s) => s.HtdocsPath ?? paths.Htdocs;

    private string SiteFile(string id) => Path.Combine(paths.Sites, id + ".json");

    private string CertDir(string id) => Path.Combine(paths.Certs, id);

    /// <summary>Records how to put <paramref name="file"/> back as it is now (or remove it if it does not exist).</summary>
    private static void Remember(string file, List<Action> undo)
    {
        var bytes = File.Exists(file) ? File.ReadAllBytes(file) : null;
        undo.Add(() =>
        {
            if (bytes is null) TryDelete(file);
            else File.WriteAllBytes(file, bytes);
        });
    }

    private static void RememberFolder(string dir, List<Action> undo)
    {
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir).ToDictionary(f => f, File.ReadAllBytes) : null;
        undo.Add(() =>
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            if (files is null) return;
            Directory.CreateDirectory(dir);
            foreach (var (file, bytes) in files) File.WriteAllBytes(file, bytes);
        });
    }

    private static void Undo(List<Action> undo)
    {
        for (var i = undo.Count - 1; i >= 0; i--)
        {
            try
            {
                undo[i]();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warning(e, "Could not restore a site file after a rejected change");
            }
        }
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Copies a folder tree. Linked folders inside it are skipped, so a junction loop cannot run forever.</summary>
    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(source))
        {
            if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            CopyTree(dir, Path.Combine(target, Path.GetFileName(dir)));
        }
    }

    /// <summary>Re-points each php.ini, runs exactly <paramref name="pools"/>, and remembers their ports.</summary>
    private async Task StartPoolsAsync(IReadOnlyList<PhpPool> pools, CancellationToken ct)
    {
        var ini = PhpIni();
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
        var ini = PhpIni();
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
            var input = RenderInputFor(s, version, pools);
            ApplyOutcome outcome;
            try
            {
                outcome = await new ConfigApplier(new ConfigRenderer(paths), new ConfigWriter(paths), tester).ApplyAsync(input, ct);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The writer swaps whole folders, so a file it cannot read or a folder it cannot move leaves data\conf as it was.
                Log.Warning(e, "Writing the Apache configuration failed");
                return new ApplyOutcome(false, [], "", $"Anvil could not write the Apache configuration ({e.Message}). Nothing was changed.");
            }
            catch (ArgumentException e)
            {
                // The renderer refuses a value from a site file edited by hand before anything is written.
                Log.Warning(e, "Building the Apache configuration failed");
                return new ApplyOutcome(false, [], "", $"Anvil could not build the Apache configuration: {e.Message} Nothing was changed.");
            }
            if (!alwaysTest || outcome.ChangedFiles.Count > 0) return outcome;
            var test = await tester.TestAsync(ct);
            return outcome with { Ok = test.Ok, TestOutput = test.Output };
        }
        finally
        {
            _apacheUnderTest = null;
        }
    }

    private RenderInput RenderInputFor(AppSettings s, string version, IReadOnlyList<PhpPool> pools)
    {
        var sites = new SiteStore(paths).All();
        return new RenderInput
        {
            Settings = s,
            Stock = StockConfScanner.Scan(File.ReadAllText(paths.HttpdConf)),
            ApacheVersion = version,
            Pools = pools,
            Sites = sites,
            Htdocs = HtdocsOf(s, sites),
            Certificates = new CertificateStore(paths).All(),
        };
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

    private static ApplyResult Rejected(ApplyOutcome applied, string consequence) => applied.WriteError is { } writeError
        ? ApplyResult.Failure(writeError)
        : ApplyResult.Failure($"Apache rejected the configuration. {consequence}", applied.TestOutput, applied.ChangedFiles);


    private static string? Validate(AppSettings s)
    {
        foreach (var (port, label) in new[] { (s.HttpPort, "HTTP"), (s.HttpsPort, "HTTPS") })
            if (port is < 1 or > 65535) return $"The {label} port must be between 1 and 65535.";
        if (s.Apache.ThreadsPerChild is < 1 or > 1920) return "Threads per child must be between 1 and 1920.";
        return s.NetworkDomain is { } domain ? NetworkNames.DomainError(domain) : null;
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

    private async Task<T> Locked<T>(Func<Task<T>> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await work();
        }
        finally
        {
            try
            {
                await RefreshNetworkCoreAsync();
            }
            finally
            {
                _gate.Release();
            }
            RaiseStatus();
        }
    }

    private void RaiseStatus() => StatusChanged?.Invoke(this, Status());

    /// <summary>
    /// Runs after every operation: the name service answers the network domain on this PC's addresses while Apache
    /// runs, and the findings say what keeps other devices out. The firewall and the networks are read only while
    /// something is open to the network.
    /// </summary>
    private async Task RefreshNetworkCoreAsync()
    {
        try
        {
            var lan = _lanAddresses();
            if (names is not null)
            {
                (string? Domain, IPAddress[] Addresses) wanted =
                    (ApacheRunning ? _settings.NetworkDomain : null, [.. lan.Select(a => a.Address).Distinct()]);
                if (_retryNames || _namesApplied is not { } applied || applied.Domain != wanted.Domain
                    || !applied.Addresses.SequenceEqual(wanted.Addresses))
                {
                    _bindFailures = await names.ApplyAsync(wanted.Domain, wanted.Addresses, CancellationToken.None);
                    _namesApplied = wanted;
                }
            }
            _retryNames = false;

            var needed = NeededFirewall();
            if (needed is null)
            {
                _networkFindings = [];
                return;
            }
            var publicIds = _publicAdapters();
            _networkFindings = NetworkFindings.For(true, FirewallReady(needed), [.. lan.Where(a => publicIds.Contains(a.AdapterId))], _bindFailures);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning("The network checks could not run: {Message}", e.Message);
        }
    }
}
