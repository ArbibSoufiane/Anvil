using ArbiPache.Core.Apache;
using ArbiPache.Core.Components;
using ArbiPache.Core.Settings;
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

    /// <summary>Ends the Agent. With stopServices false, its processes still end with it (they live in its job).</summary>
    Task ShutdownAsync(bool stopServices, CancellationToken ct = default);
}
