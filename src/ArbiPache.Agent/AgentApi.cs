using ArbiPache.Core.Agent;
using ArbiPache.Core.Apache;
using ArbiPache.Core.Components;
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

    /// <summary>Returns at once so the reply reaches the caller before the pipe closes.</summary>
    public Task ShutdownAsync(bool stopServices, CancellationToken ct = default)
    {
        requestShutdown(stopServices);
        return Task.CompletedTask;
    }
}
