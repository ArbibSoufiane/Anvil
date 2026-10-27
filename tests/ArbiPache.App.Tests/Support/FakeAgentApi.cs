using ArbiPache.Core.Agent;
using ArbiPache.Core.Apache;
using ArbiPache.Core.Components;
using ArbiPache.Core.Processes;
using ArbiPache.Core.Settings;

namespace ArbiPache.App.Tests.Support;

/// <summary>An Agent that answers from fields and records what it was asked.</summary>
public sealed class FakeAgentApi : IAgentApi
{
    public AgentStatus Status { get; set; } = new(ServerMode.Development,
        [new ServiceStatus(ServiceId.Apache, ProcessState.Stopped, "2.4.69", null)], [], "4.0.0", RunningAsService: false);

    public AppSettings Settings { get; set; } = new() { ActiveApache = "2.4.69", Apache = new ApacheTuning(256, 0, 5) };
    public AppSettings? Saved { get; private set; }
    public ApplyResult SaveResult { get; set; } = new(true, null, "Syntax OK", ["arbipache.conf"]);
    public ApplyResult NextResult { get; set; } = new(true, null, null, []);
    public ConfigTestResult TestResult { get; set; } = new(true, "Syntax OK");
    public List<ComponentRelease> Available { get; } = [];
    public List<InstalledComponent> Installed { get; } = [];
    public List<string> Calls { get; } = [];

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
        Calls.Add($"install {r.Version}");
        var installed = new InstalledComponent(r.Kind, r.Version, $@"D:\ArbiPache\runtime\apache\{r.Version}", r.Sha256, DateTimeOffset.Now);
        Installed.Add(installed);
        return Task.FromResult(installed);
    }

    public Task<ApplyResult> ActivateAsync(ComponentKind k, string version, CancellationToken ct = default)
    {
        Settings = Settings with { ActiveApache = version };
        return Record($"activate {version}");
    }

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
