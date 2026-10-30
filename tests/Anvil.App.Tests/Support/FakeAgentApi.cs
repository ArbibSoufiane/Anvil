using Anvil.Core.Agent;
using Anvil.Core.Apache;
using Anvil.Core.Certificates;
using Anvil.Core.Components;
using Anvil.Core.Database;
using Anvil.Core.Elevation;
using Anvil.Core.Php;
using Anvil.Core.Processes;
using Anvil.Core.Settings;
using Anvil.Core.Sites;

namespace Anvil.App.Tests.Support;

/// <summary>An Agent that answers from fields and records what it was asked.</summary>
public sealed class FakeAgentApi : IAgentApi
{
    public AgentStatus Status { get; set; } = new(
        [new ServiceStatus(ServiceId.Apache, ProcessState.Stopped, "2.4.69", null)], [], "4.0.0", []);

    public AppSettings Settings { get; set; } = new() { ActiveApache = "2.4.69", Apache = new ApacheTuning(256, 0, 5) };
    public AppSettings? Saved { get; private set; }
    public ApplyResult SaveResult { get; set; } = new(true, null, "Syntax OK", ["anvil.conf"]);
    public ApplyResult NextResult { get; set; } = new(true, null, null, []);
    public ConfigTestResult TestResult { get; set; } = new(true, "Syntax OK");
    public List<ComponentRelease> Available { get; } = [];
    public List<InstalledComponent> Installed { get; } = [];
    public List<UpdateOffer> Offers { get; } = [];
    public Exception? InstallError { get; set; }
    public Exception? UpdatesError { get; set; }
    public List<string> Calls { get; } = [];
    public List<PhpVersionInfo> Php { get; } = [];
    public Dictionary<string, List<PhpExtension>> Extensions { get; } = [];
    public List<SiteDefinition> Sites { get; } = [];
    public List<HtdocsEntry> Htdocs { get; } = [];
    public List<ValidationError> ValidationErrors { get; } = [];
    public List<SiteDefinition> SavedSites { get; } = [];
    public Detected? Detected { get; set; }
    public ImportReport Import { get; set; } = new([], [], []);
    public Dictionary<string, SiteCertificate> Certificates { get; } = [];
    /// <summary>php.ini values by PHP version; a version without an entry has none set.</summary>
    public Dictionary<string, PhpIniValues> PhpIni { get; } = [];
    public IReadOnlyDictionary<string, string>? IniSaved { get; private set; }
    /// <summary>Answers CheckLinkedDatabaseAsync by server id; without one, the check succeeds.</summary>
    public Dictionary<string, ApplyResult> LinkedChecks { get; } = [];
    public Exception? BackupError { get; set; }
    public string BackupFile { get; set; } = @"D:\Anvil\data\backups\db\2026-10-27-120000.sql.gz";

    public event EventHandler<AgentStatus>? StatusChanged;

    public void Raise(AgentStatus s)
    {
        Status = s;
        StatusChanged?.Invoke(this, s);
    }

    public Task<AgentStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(Status);

    public Task<ApplyResult> StartAsync(ServiceId? id, CancellationToken ct = default) => Record($"start {id}");
    public Task<ApplyResult> StopAsync(ServiceId? id, CancellationToken ct = default) => Record($"stop {id}");
    public Task<ApplyResult> RestartAsync(ServiceId? id, CancellationToken ct = default) => Record($"restart {id}");
    public Task<ConfigTestResult> TestConfigAsync(CancellationToken ct = default) => Task.FromResult(TestResult);
    public Task<AppSettings> GetSettingsAsync(CancellationToken ct = default) => Task.FromResult(Settings);

    public Task<ApplyResult> SaveSettingsAsync(AppSettings s, CancellationToken ct = default)
    {
        Saved = s;
        if (SaveResult.Ok) Settings = s;
        return Task.FromResult(SaveResult);
    }

    public Task<IReadOnlyList<ComponentRelease>> GetAvailableAsync(ComponentKind k, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ComponentRelease>>(Available.Where(r => r.Kind == k).ToList());

    public Task<IReadOnlyList<InstalledComponent>> GetInstalledAsync(ComponentKind k, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<InstalledComponent>>(Installed.Where(c => c.Kind == k).ToList());

    public Task<InstalledComponent> InstallAsync(ComponentRelease r, CancellationToken ct = default)
    {
        if (InstallError is { } error) return Task.FromException<InstalledComponent>(error);
        Calls.Add($"install {r.Version}");
        var installed = new InstalledComponent(r.Kind, r.Version, $@"D:\Anvil\runtime\{r.Kind.ToString().ToLowerInvariant()}\{r.Version}", r.Sha256,
            DateTimeOffset.Now, r.Toolset, r.AppliesTo);
        Installed.Add(installed);
        // Like the Agent, a new Xdebug build is turned on for every non-thread-safe PHP of its branch.
        if (r.Kind == ComponentKind.Xdebug)
            foreach (var php in Php.Where(p => !p.ThreadSafe && PhpSupport.Minor(p.Version) == r.AppliesTo))
                Extensions[php.Version] = [.. Extensions.GetValueOrDefault(php.Version, []).Where(e => e.Name != "xdebug"), new("xdebug", true, true, true)];
        return Task.FromResult(installed);
    }

    public Task<IReadOnlyList<UpdateOffer>> CheckUpdatesAsync(CancellationToken ct = default) =>
        UpdatesError is { } error ? Task.FromException<IReadOnlyList<UpdateOffer>>(error) : Task.FromResult<IReadOnlyList<UpdateOffer>>(Offers.ToList());

    public Task<ApplyResult> ActivateAsync(ComponentKind k, string version, CancellationToken ct = default)
    {
        Settings = Settings with { ActiveApache = version };
        return Record($"activate {version}");
    }

    public Task<IReadOnlyList<PhpVersionInfo>> GetPhpAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PhpVersionInfo>>(Php.ToList());

    public List<PhpFound> Pvm { get; } = [];

    public Task<IReadOnlyList<PhpFound>> FindPvmPhpAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PhpFound>>(Pvm.ToList());

    public Task<ApplyResult> LinkPhpAsync(string folder, CancellationToken ct = default)
    {
        if (NextResult.Ok && Pvm.FirstOrDefault(p => p.Folder == folder) is { } found)
            Php.Add(new PhpVersionInfo(found.Version, found.Toolset, 4, 1000, 0, [], ProcessState.Stopped, false, false, found.Folder, Linked: true,
                ThreadSafe: found.ThreadSafe));
        return Record($"link {folder}");
    }

    public Task<ApplyResult> UnlinkPhpAsync(string version, CancellationToken ct = default)
    {
        if (NextResult.Ok) Php.RemoveAll(p => p.Version == version);
        return Record($"unlink {version}");
    }

    public Task<ApplyResult> SetPhpPoolAsync(string version, PhpPoolSettings settings, CancellationToken ct = default) =>
        Record($"pool {version} {settings.Workers} {settings.MaxRequests}");

    public Task<ApplyResult> SetDefaultPhpAsync(string version, CancellationToken ct = default)
    {
        for (var i = 0; i < Php.Count; i++) Php[i] = Php[i] with { IsDefault = Php[i].Version == version };
        return Record($"default {version}");
    }

    public Task<IReadOnlyList<PhpExtension>> GetExtensionsAsync(string version, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PhpExtension>>(Extensions.TryGetValue(version, out var list) ? list.ToList() : []);

    public Task<ApplyResult> SetExtensionAsync(string version, string name, bool enabled, CancellationToken ct = default)
    {
        if (NextResult.Ok && Extensions.TryGetValue(version, out var list))
            for (var i = 0; i < list.Count; i++)
                if (list[i].Name == name) list[i] = list[i] with { Enabled = enabled };
        return Record($"extension {version} {name} {(enabled ? "on" : "off")}");
    }

    public Task<IReadOnlyList<SiteDefinition>> GetSitesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SiteDefinition>>(Sites.ToList());

    public Task<IReadOnlyList<HtdocsEntry>> GetHtdocsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<HtdocsEntry>>(Htdocs.ToList());

    public Task<IReadOnlyList<ValidationError>> ValidateSiteAsync(SiteDefinition s, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ValidationError>>(ValidationErrors.ToList());

    public Task<ApplyResult> SaveSiteAsync(SiteDefinition s, CancellationToken ct = default)
    {
        SavedSites.Add(s);
        if (NextResult.Ok)
        {
            Sites.RemoveAll(x => x.Id == s.Id);
            Sites.Add(s);
        }
        return Record($"save site {s.Id}");
    }

    public Task<ApplyResult> RemoveSiteAsync(string id, CancellationToken ct = default)
    {
        if (NextResult.Ok) Sites.RemoveAll(x => x.Id == id);
        return Record($"remove site {id}");
    }

    public Task<ApplyResult> SetSiteEnabledAsync(string id, bool enabled, CancellationToken ct = default) =>
        Record($"site {id} {(enabled ? "on" : "off")}");

    public Task<Detected> DetectAsync(string folder, CancellationToken ct = default) =>
        Task.FromResult(Detected ?? new Detected(SiteType.Php, folder, "PHP", null));

    public Task<ApplyResult> CopyIntoHtdocsAsync(string sourceFolder, string folderName, CancellationToken ct = default) =>
        Record($"copy {sourceFolder} {folderName}");

    public Task<ImportReport> ImportLegacyAsync(CancellationToken ct = default)
    {
        Calls.Add("import");
        return Task.FromResult(Import with { Result = NextResult });
    }

    public Task<ApplyResult> ReissueLocalCertificatesAsync(CancellationToken ct = default) => Record("reissue");

    public FirewallSetRequest? FirewallRequest { get; set; }

    public Task<FirewallSetRequest?> GetFirewallRequestAsync(CancellationToken ct = default) => Task.FromResult(FirewallRequest);

    public int RefreshNetworkCalls { get; private set; }

    public Task RefreshNetworkAsync(CancellationToken ct = default)
    {
        RefreshNetworkCalls++;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, SiteCertificate>> GetCertificatesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, SiteCertificate>>(new Dictionary<string, SiteCertificate>(Certificates));

    public List<LinkedDbInfo> LinkedDbs { get; } = [];

    /// <summary>Answers for the linked-database calls, in order; each is used once. Without one, NextResult answers.</summary>
    public Queue<ApplyResult> LinkedResults { get; } = new();

    private Task<ApplyResult> Linked(string call)
    {
        Calls.Add(call);
        return Task.FromResult(LinkedResults.TryDequeue(out var result) ? result : NextResult);
    }

    public Task<IReadOnlyList<LinkedDbInfo>> GetLinkedDatabasesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LinkedDbInfo>>(LinkedDbs.ToList());

    public Task<ApplyResult> LinkDatabaseAsync(string binDir, string user, string password, CancellationToken ct = default) =>
        Linked($"link db {binDir} {user} {password}");

    public Task<ApplyResult> UnlinkDatabaseAsync(string id, CancellationToken ct = default) => Linked($"unlink db {id}");
    public Task<ApplyResult> StartLinkedDatabaseAsync(string id, CancellationToken ct = default) => Linked($"start db {id}");
    public Task<ApplyResult> StopLinkedDatabaseAsync(string id, CancellationToken ct = default) => Linked($"stop db {id}");
    public Task<ApplyResult> SetLinkedDatabasePortAsync(string id, int port, CancellationToken ct = default) => Linked($"port db {id} {port}");

    public Task<string> BackupLinkedDatabaseAsync(string id, CancellationToken ct = default)
    {
        Calls.Add($"backup db {id}");
        return BackupError is { } error ? Task.FromException<string>(error) : Task.FromResult(BackupFile);
    }

    public Task<ApplyResult> CheckLinkedDatabaseAsync(string id, CancellationToken ct = default)
    {
        Calls.Add($"check db {id}");
        return Task.FromResult(LinkedChecks.TryGetValue(id, out var result) ? result : ApplyResult.Success());
    }

    public Task<PhpIniValues> GetPhpIniAsync(string version, CancellationToken ct = default) =>
        Task.FromResult(PhpIni.TryGetValue(version, out var ini) ? ini : new PhpIniValues(new Dictionary<string, string>(), false, false));

    public Task<ApplyResult> SavePhpIniAsync(string version, IReadOnlyDictionary<string, string> values, CancellationToken ct = default)
    {
        IniSaved = values;
        if (NextResult.Ok && PhpIni.TryGetValue(version, out var ini))
        {
            var merged = new Dictionary<string, string>(ini.Values);
            foreach (var (key, value) in values)
                if (value.Length == 0) merged.Remove(key);
                else merged[key] = value;
            PhpIni[version] = ini with { Values = merged };
        }
        return Record($"save ini {version} " + string.Join(" ", values.OrderBy(v => v.Key).Select(v => $"{v.Key}={v.Value}")));
    }

    public Task<ApplyResult> SetOpcacheAsync(string version, bool enabled, CancellationToken ct = default) =>
        Record($"opcache {version} {(enabled ? "on" : "off")}");

    public Task ShutdownAsync(bool stopServices, CancellationToken ct = default)
    {
        Calls.Add($"shutdown {stopServices}");
        return Task.CompletedTask;
    }

    private Task<ApplyResult> Record(string call)
    {
        Calls.Add(call);
        return Task.FromResult(NextResult);
    }
}
