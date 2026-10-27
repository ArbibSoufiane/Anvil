using ArbiPache.Core.Agent;
using ArbiPache.Core.Apache;
using ArbiPache.Core.Components;
using ArbiPache.Core.Php;
using ArbiPache.Core.Sites;
using ArbiPache.Core.Settings;

namespace ArbiPache.Agent;

/// <summary>What the pipe exposes: the orchestrator, plus shutting the Agent down.</summary>
public sealed class AgentApi(Orchestrator orchestrator, Action<bool> requestShutdown) : IAgentApi
{
    public event EventHandler<AgentStatus> StatusChanged
    {
        add => orchestrator.StatusChanged += value;
        remove => orchestrator.StatusChanged -= value;
    }

    public Task<AgentStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(orchestrator.Status());

    public Task<ApplyResult> StartAsync(ServiceId? id, CancellationToken ct = default) => orchestrator.StartAsync(id, ct);

    public Task<ApplyResult> StopAsync(ServiceId? id, CancellationToken ct = default) => orchestrator.StopAsync(id, ct);

    public Task<ApplyResult> RestartAsync(ServiceId? id, CancellationToken ct = default) => orchestrator.RestartAsync(id, ct);

    public Task<ConfigTestResult> TestConfigAsync(CancellationToken ct = default) => orchestrator.TestConfigAsync(ct);

    public Task<AppSettings> GetSettingsAsync(CancellationToken ct = default) => Task.FromResult(orchestrator.Settings);

    public Task<ApplyResult> SaveSettingsAsync(AppSettings s, CancellationToken ct = default) => orchestrator.SaveSettingsAsync(s, ct);

    public Task<IReadOnlyList<ComponentRelease>> GetAvailableAsync(ComponentKind k, CancellationToken ct = default) =>
        orchestrator.GetAvailableAsync(k, ct);

    public Task<IReadOnlyList<InstalledComponent>> GetInstalledAsync(ComponentKind k, CancellationToken ct = default) =>
        Task.FromResult(orchestrator.GetInstalled(k));

    public Task<InstalledComponent> InstallAsync(ComponentRelease r, CancellationToken ct = default) => orchestrator.InstallAsync(r, ct);

    public Task<ApplyResult> ActivateAsync(ComponentKind k, string version, CancellationToken ct = default) =>
        orchestrator.ActivateAsync(k, version, ct);

    public Task<IReadOnlyList<PhpVersionInfo>> GetPhpAsync(CancellationToken ct = default) => Task.FromResult(orchestrator.GetPhp());

    public Task<ApplyResult> SetPhpPoolAsync(string version, PhpPoolSettings settings, CancellationToken ct = default) =>
        orchestrator.SetPhpPoolAsync(version, settings, ct);

    public Task<ApplyResult> SetDefaultPhpAsync(string version, CancellationToken ct = default) => orchestrator.SetDefaultPhpAsync(version, ct);

    public Task<IReadOnlyList<PhpExtension>> GetExtensionsAsync(string version, CancellationToken ct = default) =>
        Task.FromResult(orchestrator.GetExtensions(version));

    public Task<ApplyResult> SetExtensionAsync(string version, string name, bool enabled, CancellationToken ct = default) =>
        orchestrator.SetExtensionAsync(version, name, enabled, ct);

    public Task<IReadOnlyList<SiteDefinition>> GetSitesAsync(CancellationToken ct = default) => Task.FromResult(orchestrator.GetSites());

    public Task<IReadOnlyList<HtdocsEntry>> GetHtdocsAsync(CancellationToken ct = default) => Task.FromResult(orchestrator.GetHtdocs());

    public Task<IReadOnlyList<ValidationError>> ValidateSiteAsync(SiteDefinition s, CancellationToken ct = default) =>
        Task.FromResult(orchestrator.ValidateSite(s));

    public Task<ApplyResult> SaveSiteAsync(SiteDefinition s, SiteSecrets? secrets, CancellationToken ct = default) =>
        orchestrator.SaveSiteAsync(s, secrets, ct);

    public Task<ApplyResult> RemoveSiteAsync(string id, CancellationToken ct = default) => orchestrator.RemoveSiteAsync(id, ct);

    public Task<ApplyResult> SetSiteEnabledAsync(string id, bool enabled, CancellationToken ct = default) =>
        orchestrator.SetSiteEnabledAsync(id, enabled, ct);

    public Task<Detected> DetectAsync(string folder, CancellationToken ct = default) => Task.FromResult(ProjectDetector.Detect(folder));

    public Task<ApplyResult> CopyIntoHtdocsAsync(string sourceFolder, string folderName, CancellationToken ct = default) =>
        orchestrator.CopyIntoHtdocsAsync(sourceFolder, folderName, ct);

    public Task<ImportReport> ImportLegacyAsync(CancellationToken ct = default) => orchestrator.ImportLegacyAsync(ct);

    /// <summary>Returns at once so the reply reaches the caller before the pipe closes.</summary>
    public Task ShutdownAsync(bool stopServices, CancellationToken ct = default)
    {
        requestShutdown(stopServices);
        return Task.CompletedTask;
    }
}
