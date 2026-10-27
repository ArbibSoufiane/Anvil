using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ArbiPache.Core;
using ArbiPache.Core.Agent;
using ArbiPache.Core.Apache;
using ArbiPache.Core.Certificates;
using ArbiPache.Core.Components;
using ArbiPache.Core.Database;
using ArbiPache.Core.Hosts;
using ArbiPache.Core.Modes;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Php;
using ArbiPache.Core.Preflight;
using ArbiPache.Core.Processes;
using ArbiPache.Core.Settings;
using ArbiPache.Core.Sites;
using Serilog;

namespace ArbiPache.Agent;

/// <summary>
/// The Agent's single point of change: every operation that touches configuration or processes runs here, one at a
/// time. A configuration Apache rejects is rolled back and never restarts anything. The PHP pools run with Apache:
/// they start just before it and stop just after it.
/// </summary>
/// <param name="portFree">True when nothing listens on the port; picks ports for new PHP pools.</param>
/// <param name="hostsText">Reads the Windows hosts file; the Agent never writes it (the Elevator does).</param>
public sealed partial class Orchestrator(AppPaths paths, SettingsStore store, IConfigTester tester, IApacheControl apache,
    Func<AppSettings, IReadOnlyList<PreflightFinding>> preflight, ComponentCatalog catalog, ComponentInstaller installer,
    IPhpPools php, Func<int, bool> portFree, bool runningAsService = false, Func<string>? hostsText = null,
    string? legacyHome = null, Func<bool, IAcmeProtocol>? acme = null, TimeProvider? time = null, IDatabaseServer? db = null)
{
    public const int MaxWorkers = 64;
    public const int MaxRecycleRequests = 1_000_000;

    private const string NotInstalled = "Install Apache first, on the Apache page.";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<string> _hostsText = hostsText ?? ReadSystemHosts;
    private readonly string _legacyHome = legacyHome ?? LegacyImporter.DefaultHome();
    private AppSettings _settings = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private IReadOnlyList<PreflightFinding> _findings = [];
    private IReadOnlyList<PreflightFinding> _expiryFindings = [];
    private readonly Dictionary<string, PreflightFinding> _renewalFailures = [];
    private string? _apacheUnderTest;

    public event EventHandler<AgentStatus>? StatusChanged;

    public AppSettings Settings => _settings;

    /// <summary>The Apache version whose httpd.exe tests the configuration: a candidate while switching, else the active one.</summary>
    public string? ApacheVersion => _apacheUnderTest ?? _settings.ActiveApache;

    private Func<bool, IAcmeProtocol> Acme => acme ?? (staging => new CertesAcmeProtocol(paths, staging, _settings.AcmeEmail));

    private bool ApacheRunning => apache.Status?.State is ProcessState.Starting or ProcessState.Running or ProcessState.Restarting;

    /// <summary>
    /// Loads settings, regenerates configuration (all of it after the folder was moved), runs the preflight checks
    /// and starts Apache when the settings ask for it and nothing blocks.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        apache.StatusChanged += (_, _) => RaiseStatus();
        if (db is not null) db.StatusChanged += (_, _) => RaiseStatus();
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
            RefreshCertificateFindings();
            if (version is not null && _settings.StartServicesOnLaunch && !findings.Any(f => f.Severity == Severity.Blocking))
            {
                var started = await StartApacheCoreAsync(version, ct);
                if (!started.Ok) Log.Warning("Apache did not start on launch: {Error}", started.Error);
            }
            if (ActiveDb is not null && _settings.StartServicesOnLaunch)
            {
                var started = await StartDbCoreAsync(ct);
                if (!started.Ok) Log.Warning("The database did not start on launch: {Error}", started.Error);
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
            DbStatus(),
        ];
        var findings = _findings.Concat(_expiryFindings).Concat(_renewalFailures.Values).ToList();
        return new AgentStatus(_settings.Mode, services, findings, BuildInfo.Version, runningAsService);
    }

    /// <summary>
    /// Apache and PHP start and stop together: either id means the web server. Null means the web server and the
    /// database, when one is installed.
    /// </summary>
    public Task<ApplyResult> StartAsync(ServiceId? id, CancellationToken ct) => Locked(async () =>
    {
        if (id is ServiceId.Database) return await StartDbCoreAsync(ct);
        var database = id is null && ActiveDb is not null ? await StartDbCoreAsync(ct) : ApplyResult.Success();
        var web = await StartWebCoreAsync(ct);
        return web.Ok ? database : web;
    }, ct);

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
        if (id is ServiceId.Database) return await StopDbCoreAsync(ct);
        await apache.StopAsync(ct);
        await php.ApplyAsync([], ct);
        if (id is null && ActiveDb is not null) return await StopDbCoreAsync(ct);
        return ApplyResult.Success();
    }, ct);

    /// <summary>
    /// Php restarts the pools' workers one at a time (php.ini changes take effect); Apache reloads Apache;
    /// null does both.
    /// </summary>
    public Task<ApplyResult> RestartAsync(ServiceId? id, CancellationToken ct) => Locked(async () =>
    {
        if (id is ServiceId.Database)
        {
            var stopped = await StopDbCoreAsync(ct);
            return stopped.Ok ? await StartDbCoreAsync(ct) : stopped;
        }
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
        var sites = new SiteStore(paths).All().Where(x => x.Enabled && x.Kind != SiteKind.Htdocs).ToList();
        return installer.Installed(ComponentKind.Php).Select(i =>
        {
            var pool = running.FirstOrDefault(p => SameVersion(p.Version, i.Version));
            var tuning = PhpPoolPlanner.TuningFor(s, i.Version);
            var isDefault = SameVersion(i.Version, s.DefaultPhp);
            var usedBy = sites.Where(x => Uses(x, i.Version, s.DefaultPhp)).Select(x => x.Name);
            return new PhpVersionInfo(i.Version, i.Toolset ?? "", tuning.Workers, tuning.MaxRequests, pool?.Port ?? 0,
                (isDefault ? usedBy.Prepend("htdocs") : usedBy).ToList(), pool is null ? ProcessState.Stopped : php.StateOf(pool.Key), isDefault,
                PhpSupport.IsEndOfLife(i.Version, today));
        }).ToList();

        static bool Uses(SiteDefinition site, string version, string? defaultPhp) =>
            (site.Type == SiteType.Php && SameVersion(version, site.PhpVersion ?? defaultPhp))
            || site.Mounts.Any(m => m.Type == SiteType.Php && SameVersion(version, m.PhpVersion ?? site.PhpVersion ?? defaultPhp));
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
    /// Validates, writes the password file and a local development certificate when needed, saves the site, then keeps
    /// it only if Apache accepts the configuration; a rejected change puts every file back as it was.
    /// </summary>
    public Task<ApplyResult> SaveSiteAsync(SiteDefinition s, SiteSecrets? secrets, CancellationToken ct) => Locked(async () =>
    {
        var sites = new SiteStore(paths);
        var (site, renamedFrom, others) = Prepare(s, sites.All());
        if (ValidateAgainst(site, others) is { Count: > 0 } errors) return ApplyResult.Failure(string.Join(" ", errors.Select(e => e.Message)));

        var htpasswd = HtpasswdPath(site.Id);
        var oldHtpasswd = renamedFrom is null ? null : HtpasswdPath(renamedFrom);
        var newSecrets = secrets is { User.Length: > 0, Password.Length: > 0 };
        if (site.PasswordProtected && !newSecrets && !File.Exists(htpasswd) && !File.Exists(oldHtpasswd))
            return ApplyResult.Failure("Enter a user name and password for this site.");

        var undo = new List<Action>();
        var certs = new CertificateStore(paths);
        try
        {
            if (newSecrets)
            {
                Remember(htpasswd, undo);
                HtpasswdFile.Write(htpasswd, secrets!.User!, secrets.Password!);
            }
            else if (site.PasswordProtected && !File.Exists(htpasswd) && File.Exists(oldHtpasswd))
            {
                Remember(htpasswd, undo);
                File.Copy(oldHtpasswd!, htpasswd);
            }

            if (renamedFrom is not null && site.Cert is CertSource.Imported or CertSource.LetsEncrypt
                && certs.Get(site.Id) is null && certs.Get(renamedFrom) is not null)
            {
                RememberFolder(CertDir(site.Id), undo);
                CopyTree(CertDir(renamedFrom), CertDir(site.Id));
            }

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
        if (!site.PasswordProtected) TryDelete(htpasswd);
        if (renamedFrom is not null)
        {
            TryDelete(oldHtpasswd!);
            certs.Delete(renamedFrom);
        }

        return result with { HostsDomains = HostsChange() };
    }, ct);

    /// <summary>Removes the site with its certificate and password file. Its logs stay.</summary>
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
        TryDelete(HtpasswdPath(id));
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
        return applied ? result with { HostsDomains = HostsChange() } : result;
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
    /// <summary>
    /// Renews the certificates <see cref="RenewalPlanner"/> finds due and reloads Apache when any changed. A renewal that
    /// fails keeps the certificate in use and becomes a finding, as does an imported certificate close to expiry.
    /// </summary>
    public Task<RenewalReport> RenewCertificatesAsync(CancellationToken ct) => Locked(async () =>
    {
        var sites = new SiteStore(paths).All();
        var certs = new CertificateStore(paths);
        var renewed = new List<string>();
        var failed = new List<string>();
        var undo = new List<Action>();
        foreach (var decision in RenewalPlanner.Plan(sites, certs.All(), _time.GetUtcNow()))
        {
            if (decision.Action is not (RenewalAction.RenewLetsEncrypt or RenewalAction.ReissueLocal)) continue;
            var site = sites.First(s => s.Id == decision.SiteId);
            try
            {
                RememberFolder(CertDir(site.Id), undo);
                if (decision.Action == RenewalAction.ReissueLocal) new LocalCa(paths, certs, _time).Issue(site.Id, CertNames(site));
                else await new AcmeService(Acme, certs, paths).IssueAsync(site.Id, CertNames(site), _settings.AcmeStaging, ct);
                renewed.Add(site.Id);
                _renewalFailures.Remove(site.Id);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.Warning(e, "The certificate for {Site} was not renewed", site.Id);
                failed.Add(site.Id);
                _renewalFailures[site.Id] = RenewalFailed(site, e.Message);
            }
        }

        if (renewed.Count > 0)
        {
            var (applied, result) = await ApplySitesAsync(undo, "The previous certificates stay in use.", ct);
            if (!applied)
            {
                foreach (var id in renewed) _renewalFailures[id] = RenewalFailed(sites.First(s => s.Id == id), result.Error ?? "Apache rejected it.");
                failed.AddRange(renewed);
                renewed.Clear();
            }
        }

        RefreshCertificateFindings();
        RaiseStatus();
        return new RenewalReport(renewed, failed);
    }, ct);

    private static PreflightFinding RenewalFailed(SiteDefinition site, string reason) =>
        new($"cert-{site.Id}", Severity.Warning, $"The certificate for {site.Name} was not renewed. {reason}",
            $"ArbiPache tries again in {RenewalWorker.RetryAfter.TotalHours:0} hours.");

    /// <summary>Imported certificates close to expiry become findings; failures of sites no longer due are dropped.</summary>
    private void RefreshCertificateFindings()
    {
        var sites = new SiteStore(paths).All();
        var plan = RenewalPlanner.Plan(sites, new CertificateStore(paths).All(), _time.GetUtcNow());
        _expiryFindings = plan.Where(d => d.Action == RenewalAction.WarnImported)
            .Select(d => ExpiryFinding(sites.First(s => s.Id == d.SiteId), d.DaysLeft))
            .ToList();
        var due = plan.Where(d => d.Action is RenewalAction.RenewLetsEncrypt or RenewalAction.ReissueLocal).Select(d => d.SiteId).ToHashSet();
        foreach (var id in _renewalFailures.Keys.Where(id => !due.Contains(id)).ToList()) _renewalFailures.Remove(id);
    }

    private static PreflightFinding ExpiryFinding(SiteDefinition site, int daysLeft) =>
        new($"cert-{site.Id}", daysLeft <= 7 ? Severity.Warning : Severity.Info, daysLeft switch
        {
            < 0 => $"The certificate for {site.Name} has expired.",
            0 => $"The certificate for {site.Name} expires today.",
            1 => $"The certificate for {site.Name} expires tomorrow.",
            _ => $"The certificate for {site.Name} expires in {daysLeft} days.",
        }, "Ask the client for the renewed certificate and import it in the site's settings.");


    public IReadOnlyDictionary<string, SiteCertificate> GetCertificates() => new CertificateStore(paths).All();

    /// <summary>Stores the certificate a client sent for a saved site and reloads Apache. A refused certificate changes nothing.</summary>
    public Task<SiteCertificate> ImportCertificateAsync(string siteId, CertImportRequest r, CancellationToken ct) => Locked(() =>
        ReplaceCertificateAsync(siteId, CertSource.Imported, (site, certs) =>
            Task.FromResult(new CertImporter(certs, _time).Import(site.Id, CertNames(site), r)), ct), ct);

    /// <summary>Orders the saved site's certificate from Let's Encrypt and reloads Apache. A failed order changes nothing.</summary>
    public Task<SiteCertificate> IssueLetsEncryptAsync(string siteId, bool staging, CancellationToken ct) => Locked(() =>
        ReplaceCertificateAsync(siteId, CertSource.LetsEncrypt, (site, certs) =>
            new AcmeService(Acme, certs, paths).IssueAsync(site.Id, CertNames(site), staging, ct), ct), ct);

    private async Task<SiteCertificate> ReplaceCertificateAsync(string siteId, CertSource source,
        Func<SiteDefinition, CertificateStore, Task<SiteCertificate>> obtain, CancellationToken ct)
    {
        var site = new SiteStore(paths).All().FirstOrDefault(s => s.Id == siteId)
            ?? throw new InvalidOperationException($"There is no site {siteId}. Save the site first.");
        if (site.Cert != source) throw new InvalidOperationException($"{site.Name} does not take this kind of certificate.");

        var undo = new List<Action>();
        RememberFolder(CertDir(site.Id), undo);
        SiteCertificate saved;
        try
        {
            saved = await obtain(site, new CertificateStore(paths));
        }
        catch
        {
            Undo(undo);
            throw;
        }

        var (applied, result) = await ApplySitesAsync(undo, "The previous certificate stays in use.", ct);
        if (!applied) throw new InvalidOperationException(string.Join("\n", new[] { result.Error, result.ConfigTestOutput }.Where(t => !string.IsNullOrWhiteSpace(t))));
        RaiseStatus();
        return saved;
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

    // htdocs needs the default version; enabled sites, mounts and htdocs folders add theirs.
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
        var errors = SiteValidator.Validate(site, others).ToList();
        if (site.Type == SiteType.Php && site.PhpVersion is { } version && InstalledPhp(version) is null && errors.All(e => e.Field != "PhpVersion"))
            errors.Add(new ValidationError("PhpVersion", $"PHP {version} is not installed. Install it on the PHP page."));
        if (site.Mounts.FirstOrDefault(m => m.Type == SiteType.Php && m.PhpVersion is { } v && InstalledPhp(v) is null) is { } mount
            && errors.All(e => e.Field != "Mounts"))
            errors.Add(new ValidationError("Mounts", $"{mount.UrlPath}: PHP {mount.PhpVersion} is not installed."));
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

        RefreshCertificateFindings();
        if (!ApacheRunning) return (true, ApplyResult.Success(applied.ChangedFiles, applied.TestOutput));
        return (true, await Guard(async () =>
        {
            await StartPoolsAsync(pools, ct);
            await apache.GracefulRestartAsync(ct);
        }, applied));
    }

    /// <summary>
    /// The hosts entries the sites need, when they differ from ArbiPache's block in the hosts file now or ArbiPache 3's block
    /// still lists one of them; else null.
    /// </summary>
    private IReadOnlyList<string>? HostsChange()
    {
        var wanted = new SiteStore(paths).All()
            .Where(s => s.Enabled && s.Kind == SiteKind.Domain)
            .SelectMany(s => s.Aliases.Prepend(s.Name)
                .Select(n => SiteSet.NormalizeHost(n) ?? n)
                .Where(n => s.AddToHosts || n.EndsWith(".test", StringComparison.Ordinal)))
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

    /// <summary>Domain sites: the domain and its aliases. IP sites: the address; all addresses: localhost.</summary>
    private static IReadOnlyList<string> CertNames(SiteDefinition site) => site.Kind switch
    {
        SiteKind.Domain => site.Aliases.Prepend(site.Name).Select(n => SiteSet.NormalizeHost(n) ?? n).Distinct(StringComparer.Ordinal).ToList(),
        _ when site.Name == "*" => ["localhost"],
        _ => [site.Name],
    };

    private static bool NeedsLocalCertificate(SiteCertificate? current, IReadOnlyList<string> names) =>
        current is null || current.Source != CertSource.LocalDev || current.NotAfter < DateTimeOffset.UtcNow.AddDays(30)
        || !names.All(n => current.Names.Contains(n, StringComparer.OrdinalIgnoreCase));

    private IReadOnlyList<HtdocsEntry> HtdocsOf(AppSettings s, IReadOnlyList<SiteDefinition> sites) =>
        SiteSet.DiscoverHtdocs(HtdocsDir(s), sites.Where(x => x.Kind == SiteKind.Htdocs).ToList());

    private string HtdocsDir(AppSettings s) => s.HtdocsPath ?? paths.Htdocs;

    private string SiteFile(string id) => Path.Combine(paths.Sites, id + ".json");

    private string HtpasswdPath(string id) => Path.Combine(paths.Auth, id + ".htpasswd");

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
            var sites = new SiteStore(paths).All();
            var input = new RenderInput
            {
                Settings = s,
                Stock = StockConfScanner.Scan(await File.ReadAllTextAsync(paths.HttpdConf, ct)),
                Profile = BuiltInProfiles.For(s.Mode),
                ApacheVersion = version,
                Pools = pools,
                Sites = sites,
                Htdocs = HtdocsOf(s, sites),
                Certificates = new CertificateStore(paths).All(),
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

    private async Task<T> Locked<T>(Func<Task<T>> work, CancellationToken ct)
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
