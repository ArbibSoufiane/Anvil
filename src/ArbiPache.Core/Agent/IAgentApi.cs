using ArbiPache.Core.Apache;
using ArbiPache.Core.Components;
using ArbiPache.Core.Php;
using ArbiPache.Core.Settings;
using ArbiPache.Core.Sites;
using StreamJsonRpc;

namespace ArbiPache.Core.Agent;

/// <summary>What the GUI may ask of the Agent, over the named pipe <see cref="AgentPipe.Name"/>.</summary>
[JsonRpcContract]
public partial interface IAgentApi
{
    event EventHandler<AgentStatus> StatusChanged;

    Task<AgentStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>A null id means every service.</summary>
    Task<ApplyResult> StartAsync(ServiceId? id, CancellationToken ct = default);

    Task<ApplyResult> StopAsync(ServiceId? id, CancellationToken ct = default);
    Task<ApplyResult> RestartAsync(ServiceId? id, CancellationToken ct = default);
    Task<ConfigTestResult> TestConfigAsync(CancellationToken ct = default);
    Task<AppSettings> GetSettingsAsync(CancellationToken ct = default);

    /// <summary>Settings are kept only when the configuration they produce passes Apache's test.</summary>
    Task<ApplyResult> SaveSettingsAsync(AppSettings s, CancellationToken ct = default);

    Task<IReadOnlyList<ComponentRelease>> GetAvailableAsync(ComponentKind k, CancellationToken ct = default);
    Task<IReadOnlyList<InstalledComponent>> GetInstalledAsync(ComponentKind k, CancellationToken ct = default);
    Task<InstalledComponent> InstallAsync(ComponentRelease r, CancellationToken ct = default);
    Task<ApplyResult> ActivateAsync(ComponentKind k, string version, CancellationToken ct = default);

    /// <summary>Installed PHP versions, newest first.</summary>
    Task<IReadOnlyList<PhpVersionInfo>> GetPhpAsync(CancellationToken ct = default);

    Task<ApplyResult> SetPhpPoolAsync(string version, PhpPoolSettings settings, CancellationToken ct = default);
    Task<ApplyResult> SetDefaultPhpAsync(string version, CancellationToken ct = default);
    Task<IReadOnlyList<PhpExtension>> GetExtensionsAsync(string version, CancellationToken ct = default);

    /// <summary>Restarts that version's pool only.</summary>
    Task<ApplyResult> SetExtensionAsync(string version, string name, bool enabled, CancellationToken ct = default);

    Task<IReadOnlyList<SiteDefinition>> GetSitesAsync(CancellationToken ct = default);

    /// <summary>What localhost/&lt;folder&gt; serves: htdocs folders and htdocs entries kept elsewhere.</summary>
    Task<IReadOnlyList<HtdocsEntry>> GetHtdocsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ValidationError>> ValidateSiteAsync(SiteDefinition s, CancellationToken ct = default);

    /// <summary>
    /// Kept only when Apache accepts the result. HostsDomains in the result, when set, is the list the GUI must hand to
    /// the Elevator. Secrets are needed the first time a site is password protected.
    /// </summary>
    Task<ApplyResult> SaveSiteAsync(SiteDefinition s, SiteSecrets? secrets, CancellationToken ct = default);

    Task<ApplyResult> RemoveSiteAsync(string id, CancellationToken ct = default);
    Task<ApplyResult> SetSiteEnabledAsync(string id, bool enabled, CancellationToken ct = default);
    Task<Detected> DetectAsync(string folder, CancellationToken ct = default);

    /// <summary>Refuses a folder name htdocs already has.</summary>
    Task<ApplyResult> CopyIntoHtdocsAsync(string sourceFolder, string folderName, CancellationToken ct = default);

    /// <summary>Brings in ArbiPache 3's sites. Result.HostsDomains, when set, is the list to hand to the Elevator.</summary>
    Task<ImportReport> ImportLegacyAsync(CancellationToken ct = default);

    /// <summary>Ends the Agent. With stopServices false, its processes still end with it (they live in its job).</summary>
    Task ShutdownAsync(bool stopServices, CancellationToken ct = default);
}
