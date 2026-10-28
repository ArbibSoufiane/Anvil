using System.Diagnostics;
using Anvil.Core.Agent;
using Anvil.Core.Apache;
using Anvil.Core.Certificates;
using Anvil.Core.Components;
using Anvil.Core.Database;
using Anvil.Core.Modes;
using Anvil.Core.Paths;
using Anvil.Core.Php;
using Anvil.Core.Settings;
using Anvil.Core.Sites;
using Serilog;

namespace Anvil.App.Services;

/// <summary>
/// The GUI's link to the Agent. Tries the pipe for 2 s; when nobody answers, starts the AnvilAgent service when it
/// is installed, else Anvil.Agent.exe --child, and waits up to 10 s more. When the Agent goes away it keeps
/// reconnecting. <see cref="ConnectAsync"/> returns one stable <see cref="IAgentApi"/> that always talks to the current
/// connection, so view models subscribe once.
/// </summary>
public sealed class AgentConnection : IAsyncDisposable, IAgentLink
{
    private static readonly TimeSpan Probe = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AfterStart = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StopWithin = TimeSpan.FromMinutes(1);

    private readonly string _pipeName;
    private readonly string _agentExe;
    private readonly string _mutexName;
    private readonly Func<bool> _startAgent;
    private readonly CancellationTokenSource _life = new();
    private readonly Lock _lock = new();
    private Task<AgentClient>? _current;
    private int _holds;

    /// <param name="startAgent">Starts the Agent; returns false when it could not. Null starts the service or Anvil.Agent.exe.</param>
    /// <param name="mutexName">The mutex a running Agent holds.</param>
    /// <param name="agentExe">The program that must serve the pipe; null means this folder's Anvil.Agent.exe.</param>
    public AgentConnection(AppPaths paths, string pipeName = AgentPipe.Name, Func<bool>? startAgent = null, string mutexName = AgentPipe.Mutex,
        IAgentService? service = null, string? agentExe = null)
    {
        _pipeName = pipeName;
        _agentExe = agentExe ?? Path.Combine(paths.Root, "Anvil.Agent.exe");
        _mutexName = mutexName;
        var windowsService = service ?? new WindowsAgentService();
        _startAgent = startAgent ?? (() => StartAgent(windowsService, () => StartAgentProcess(paths)));
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

    async Task IAgentLink.ConnectAsync(CancellationToken ct) => await ConnectAsync(ct);

    public IDisposable HoldStart()
    {
        Interlocked.Increment(ref _holds);
        return new Hold(this);
    }

    public async Task WhenAgentGoneAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + StopWithin;
        while (AgentRunning(_mutexName))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The Anvil Agent did not stop within a minute.");
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
    }

    /// <summary>The service when Windows has it, else the child process. False when neither could be started.</summary>
    internal static bool StartAgent(IAgentService service, Func<bool> startChild)
    {
        if (!service.IsInstalled()) return startChild();
        try
        {
            service.Start();
            Log.Information("Started the Anvil service");
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Warning(e, "Windows did not start the Anvil service");
            return false;
        }
    }

    // Existence marks a running Agent; access denied means it runs under another account (the service).
    private static bool AgentRunning(string mutexName)
    {
        try
        {
            if (!Mutex.TryOpenExisting(mutexName, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
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
            client = await AgentRpc.ConnectAsync(_pipeName, Probe, _life.Token, _agentExe);
        }
        catch (TimeoutException)
        {
            // The service switch is moving the Agent and decides which one runs next.
            if (Volatile.Read(ref _holds) > 0) throw new InvalidOperationException("The Anvil Agent is being moved.");
            if (!_startAgent()) throw new InvalidOperationException("Anvil.Agent.exe could not be started.");
            client = await AgentRpc.ConnectAsync(_pipeName, AfterStart, _life.Token, _agentExe);
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
        var exe = Path.Combine(AppContext.BaseDirectory, "Anvil.Agent.exe");
        if (!File.Exists(exe)) return false;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = paths.Root };
        psi.ArgumentList.Add("--child");
        using var process = Process.Start(psi);
        Log.Information("Started the Agent (PID {Pid})", process?.Id);
        return process is not null;
    }

    private sealed class Hold(AgentConnection owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) Interlocked.Decrement(ref owner._holds);
        }
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

        public async Task<IReadOnlyList<UpdateOffer>> CheckUpdatesAsync(CancellationToken ct = default) =>
            await (await Remote()).CheckUpdatesAsync(ct);

        public async Task<IReadOnlyList<PhpVersionInfo>> GetPhpAsync(CancellationToken ct = default) => await (await Remote()).GetPhpAsync(ct);

        public async Task<ApplyResult> SetPhpPoolAsync(string version, PhpPoolSettings settings, CancellationToken ct = default) =>
            await (await Remote()).SetPhpPoolAsync(version, settings, ct);

        public async Task<ApplyResult> SetDefaultPhpAsync(string version, CancellationToken ct = default) =>
            await (await Remote()).SetDefaultPhpAsync(version, ct);

        public async Task<IReadOnlyList<PhpExtension>> GetExtensionsAsync(string version, CancellationToken ct = default) =>
            await (await Remote()).GetExtensionsAsync(version, ct);

        public async Task<ApplyResult> SetExtensionAsync(string version, string name, bool enabled, CancellationToken ct = default) =>
            await (await Remote()).SetExtensionAsync(version, name, enabled, ct);

        public async Task<IReadOnlyList<SiteDefinition>> GetSitesAsync(CancellationToken ct = default) => await (await Remote()).GetSitesAsync(ct);

        public async Task<IReadOnlyList<HtdocsEntry>> GetHtdocsAsync(CancellationToken ct = default) => await (await Remote()).GetHtdocsAsync(ct);

        public async Task<IReadOnlyList<ValidationError>> ValidateSiteAsync(SiteDefinition s, CancellationToken ct = default) =>
            await (await Remote()).ValidateSiteAsync(s, ct);

        public async Task<ApplyResult> SaveSiteAsync(SiteDefinition s, SiteSecrets? secrets, CancellationToken ct = default) =>
            await (await Remote()).SaveSiteAsync(s, secrets, ct);

        public async Task<ApplyResult> RemoveSiteAsync(string id, CancellationToken ct = default) => await (await Remote()).RemoveSiteAsync(id, ct);

        public async Task<ApplyResult> SetSiteEnabledAsync(string id, bool enabled, CancellationToken ct = default) =>
            await (await Remote()).SetSiteEnabledAsync(id, enabled, ct);

        public async Task<Detected> DetectAsync(string folder, CancellationToken ct = default) => await (await Remote()).DetectAsync(folder, ct);

        public async Task<ApplyResult> CopyIntoHtdocsAsync(string sourceFolder, string folderName, CancellationToken ct = default) =>
            await (await Remote()).CopyIntoHtdocsAsync(sourceFolder, folderName, ct);

        public async Task<ImportReport> ImportLegacyAsync(CancellationToken ct = default) => await (await Remote()).ImportLegacyAsync(ct);

        public async Task<ApplyResult> ReissueLocalCertificatesAsync(CancellationToken ct = default) =>
            await (await Remote()).ReissueLocalCertificatesAsync(ct);

        public async Task<IReadOnlyDictionary<string, SiteCertificate>> GetCertificatesAsync(CancellationToken ct = default) =>
            await (await Remote()).GetCertificatesAsync(ct);

        public async Task<SiteCertificate> ImportCertificateAsync(string siteId, CertImportRequest r, CancellationToken ct = default) =>
            await (await Remote()).ImportCertificateAsync(siteId, r, ct);

        public async Task<SiteCertificate> IssueLetsEncryptAsync(string siteId, bool staging, CancellationToken ct = default) =>
            await (await Remote()).IssueLetsEncryptAsync(siteId, staging, ct);

        public async Task<IReadOnlyList<string>> ResolveAsync(string name, CancellationToken ct = default) => await (await Remote()).ResolveAsync(name, ct);

        public async Task<ApplyResult> InstallDatabaseAsync(DbEngine e, string version, string rootPassword, CancellationToken ct = default) =>
            await (await Remote()).InstallDatabaseAsync(e, version, rootPassword, ct);

        public async Task<IReadOnlyList<DbSettingRow>> GetDbSettingsAsync(CancellationToken ct = default) => await (await Remote()).GetDbSettingsAsync(ct);

        public async Task<ApplyResult> SaveDbSettingsAsync(IReadOnlyDictionary<string, string?> changes, CancellationToken ct = default) =>
            await (await Remote()).SaveDbSettingsAsync(changes, ct);

        public async Task<ApplyResult> SetDbRootPasswordAsync(string password, CancellationToken ct = default) =>
            await (await Remote()).SetDbRootPasswordAsync(password, ct);

        public async Task<string> BackupDatabaseAsync(CancellationToken ct = default) => await (await Remote()).BackupDatabaseAsync(ct);

        public async Task<ModeChangeSet> PreviewModeAsync(ServerMode target, CancellationToken ct = default) =>
            await (await Remote()).PreviewModeAsync(target, ct);

        public async Task<ApplyResult> ApplyModeAsync(ServerMode target, ModeOptions options, CancellationToken ct = default) =>
            await (await Remote()).ApplyModeAsync(target, options, ct);

        public async Task<IReadOnlyList<ModeProfile>> GetModeProfilesAsync(CancellationToken ct = default) =>
            await (await Remote()).GetModeProfilesAsync(ct);

        public async Task<ApplyResult> SaveModeProfileAsync(ServerMode mode, ModeProfileOverrides overrides, CancellationToken ct = default) =>
            await (await Remote()).SaveModeProfileAsync(mode, overrides, ct);

        public async Task ShutdownAsync(bool stopServices, CancellationToken ct = default) => await (await Remote()).ShutdownAsync(stopServices, ct);
    }
}
