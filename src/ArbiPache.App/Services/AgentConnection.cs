using System.Diagnostics;
using ArbiPache.Core.Agent;
using ArbiPache.Core.Apache;
using ArbiPache.Core.Components;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Php;
using ArbiPache.Core.Settings;
using Serilog;

namespace ArbiPache.App.Services;

/// <summary>
/// The GUI's link to the Agent. Tries the pipe for 2 s; when nobody answers, starts ArbiPache.Agent.exe --child and
/// waits up to 10 s more. When the Agent goes away it keeps reconnecting. <see cref="ConnectAsync"/> returns one
/// stable <see cref="IAgentApi"/> that always talks to the current connection, so view models subscribe once.
/// </summary>
public sealed class AgentConnection : IAsyncDisposable
{
    private static readonly TimeSpan Probe = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AfterStart = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(2);

    private readonly string _pipeName;
    private readonly Func<bool> _startAgent;
    private readonly CancellationTokenSource _life = new();
    private readonly Lock _lock = new();
    private Task<AgentClient>? _current;

    /// <param name="startAgent">Starts the Agent; returns false when it could not. Null starts ArbiPache.Agent.exe.</param>
    public AgentConnection(AppPaths paths, string pipeName = AgentPipe.Name, Func<bool>? startAgent = null)
    {
        _pipeName = pipeName;
        _startAgent = startAgent ?? (() => StartAgentProcess(paths));
        Api = new ForwardingApi(this);
    }

    public IAgentApi Api { get; }

    public event EventHandler? Disconnected;
    public event EventHandler? Reconnected;

    public async Task<IAgentApi> ConnectAsync(CancellationToken ct)
    {
        await CurrentAsync().WaitAsync(ct);
        return Api;
    }

    public async ValueTask DisposeAsync()
    {
        await _life.CancelAsync();
        Task<AgentClient>? current;
        lock (_lock) current = _current;
        if (current is { IsCompletedSuccessfully: true }) await current.Result.DisposeAsync();
        _life.Dispose();
    }

    private Task<AgentClient> CurrentAsync()
    {
        lock (_lock)
        {
            if (_current is null || _current.IsFaulted || _current.IsCanceled) _current = ConnectCoreAsync();
            return _current;
        }
    }

    private async Task<AgentClient> ConnectCoreAsync()
    {
        AgentClient client;
        try
        {
            client = await AgentRpc.ConnectAsync(_pipeName, Probe, _life.Token);
        }
        catch (TimeoutException)
        {
            if (!_startAgent()) throw new InvalidOperationException("ArbiPache.Agent.exe could not be started.");
            client = await AgentRpc.ConnectAsync(_pipeName, AfterStart, _life.Token);
        }
        client.Api.StatusChanged += (_, s) => ((ForwardingApi)Api).Raise(s);
        _ = client.Completion.ContinueWith(_ => OnLost(client), TaskScheduler.Default);
        return client;
    }

    private void OnLost(AgentClient client)
    {
        if (_life.IsCancellationRequested) return;
        lock (_lock)
        {
            if (_current is { IsCompletedSuccessfully: true } t && t.Result == client) _current = null;
        }
        Log.Warning("Lost the connection to the Agent; reconnecting");
        Disconnected?.Invoke(this, EventArgs.Empty);
        _ = ReconnectAsync();
    }

    private async Task ReconnectAsync()
    {
        while (!_life.IsCancellationRequested)
        {
            try
            {
                await CurrentAsync();
                Reconnected?.Invoke(this, EventArgs.Empty);
                return;
            }
            catch (Exception e) when (e is TimeoutException or InvalidOperationException or IOException)
            {
                Log.Warning("Agent not reachable yet: {Message}", e.Message);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try { await Task.Delay(RetryEvery, _life.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    // Not in any job of the GUI's: the Agent keeps serving after the window is closed.
    private static bool StartAgentProcess(AppPaths paths)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "ArbiPache.Agent.exe");
        if (!File.Exists(exe)) return false;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = paths.Root };
        psi.ArgumentList.Add("--child");
        using var process = Process.Start(psi);
        Log.Information("Started the Agent (PID {Pid})", process?.Id);
        return process is not null;
    }

    private sealed class ForwardingApi(AgentConnection owner) : IAgentApi
    {
        public event EventHandler<AgentStatus>? StatusChanged;

        public void Raise(AgentStatus s) => StatusChanged?.Invoke(this, s);

        private async Task<IAgentApi> Remote() => (await owner.CurrentAsync()).Api;

        public async Task<AgentStatus> GetStatusAsync(CancellationToken ct = default) => await (await Remote()).GetStatusAsync(ct);
        public async Task<ApplyResult> StartAsync(ServiceId? id, CancellationToken ct = default) => await (await Remote()).StartAsync(id, ct);
        public async Task<ApplyResult> StopAsync(ServiceId? id, CancellationToken ct = default) => await (await Remote()).StopAsync(id, ct);
        public async Task<ApplyResult> RestartAsync(ServiceId? id, CancellationToken ct = default) => await (await Remote()).RestartAsync(id, ct);
        public async Task<ConfigTestResult> TestConfigAsync(CancellationToken ct = default) => await (await Remote()).TestConfigAsync(ct);
        public async Task<AppSettings> GetSettingsAsync(CancellationToken ct = default) => await (await Remote()).GetSettingsAsync(ct);
        public async Task<ApplyResult> SaveSettingsAsync(AppSettings s, CancellationToken ct = default) => await (await Remote()).SaveSettingsAsync(s, ct);

        public async Task<IReadOnlyList<ComponentRelease>> GetAvailableAsync(ComponentKind k, CancellationToken ct = default) =>
            await (await Remote()).GetAvailableAsync(k, ct);

        public async Task<IReadOnlyList<InstalledComponent>> GetInstalledAsync(ComponentKind k, CancellationToken ct = default) =>
            await (await Remote()).GetInstalledAsync(k, ct);

        public async Task<InstalledComponent> InstallAsync(ComponentRelease r, CancellationToken ct = default) => await (await Remote()).InstallAsync(r, ct);

        public async Task<ApplyResult> ActivateAsync(ComponentKind k, string version, CancellationToken ct = default) =>
            await (await Remote()).ActivateAsync(k, version, ct);

        public async Task<IReadOnlyList<PhpVersionInfo>> GetPhpAsync(CancellationToken ct = default) => await (await Remote()).GetPhpAsync(ct);

        public async Task<ApplyResult> SetPhpPoolAsync(string version, PhpPoolSettings settings, CancellationToken ct = default) =>
            await (await Remote()).SetPhpPoolAsync(version, settings, ct);

        public async Task<ApplyResult> SetDefaultPhpAsync(string version, CancellationToken ct = default) =>
            await (await Remote()).SetDefaultPhpAsync(version, ct);

        public async Task<IReadOnlyList<PhpExtension>> GetExtensionsAsync(string version, CancellationToken ct = default) =>
            await (await Remote()).GetExtensionsAsync(version, ct);

        public async Task<ApplyResult> SetExtensionAsync(string version, string name, bool enabled, CancellationToken ct = default) =>
            await (await Remote()).SetExtensionAsync(version, name, enabled, ct);

        public async Task ShutdownAsync(bool stopServices, CancellationToken ct = default) => await (await Remote()).ShutdownAsync(stopServices, ct);
    }
}
