using System.Collections.ObjectModel;
using ArbiPache.App.Services;
using ArbiPache.Core.Agent;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Preflight;
using ArbiPache.Core.Processes;
using ArbiPache.Core.Settings;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace ArbiPache.App.ViewModels;

public sealed record FindingItem(string Message, string? Fix, bool IsBlocking);

/// <summary>The Dashboard: the Apache card, problems found before start, where to open the sites, and the mode.</summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    // Copy from the mockup's "Current mode" card.
    private static readonly IReadOnlyList<string> DevelopmentLines =
    [
        "PHP errors shown in the page, every notice reported",
        "Scripts may run 300 s, 1 GB memory, 512 MB uploads",
        "OPcache re-checks files on every request",
        "Xdebug loaded, directory listings on",
    ];

    private static readonly IReadOnlyList<string> ProductionLines =
    [
        "Errors logged, never shown; version headers hidden",
        "Scripts stopped at 30 s, 256 MB memory, 32 MB uploads",
        "Slow clients cut off (request read timeout), queries capped at 60 s",
        "Shell functions disabled, directory listings off",
    ];

    private readonly IAgentApi _api;
    private readonly AppPaths _paths;
    private readonly EditorLauncher _editor;
    private readonly Action<Action> _ui;
    private AppSettings _settings = new();

    public DashboardViewModel(IAgentApi api, AppPaths paths, EditorLauncher editor, Action<Action>? ui = null)
    {
        _api = api;
        _paths = paths;
        _editor = editor;
        _ui = ui ?? (a => Dispatcher.UIThread.Post(a));
        api.StatusChanged += (_, s) =>
        {
            _ui(() => Apply(s));
            _ = ReloadSettingsAsync();
        };
        ApplySettings();
        Apply(new AgentStatus(ServerMode.Development, [], [], "", false));
    }

    [ObservableProperty]
    public partial string ApacheStateText { get; set; } = "Stopped";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartStopLabel))]
    public partial bool ApacheRunning { get; set; }

    [ObservableProperty]
    public partial bool ApacheFailed { get; set; }

    [ObservableProperty]
    public partial string ApacheVersion { get; set; } = "Not installed";

    [ObservableProperty]
    public partial string ApacheWorkers { get; set; } = "";

    [ObservableProperty]
    public partial string ApachePorts { get; set; } = "";

    /// <summary>The last error from a Start/Stop/Restart, or Apache's last error after it failed.</summary>
    [ObservableProperty]
    public partial string? ApacheMessage { get; set; }

    [ObservableProperty]
    public partial string LocalUrl { get; set; } = "http://localhost/";

    [ObservableProperty]
    public partial string LocalNote { get; set; } = "";

    [ObservableProperty]
    public partial string? NetworkUrl { get; set; }

    [ObservableProperty]
    public partial string NetworkNote { get; set; } = "";

    [ObservableProperty]
    public partial string ModeName { get; set; } = "Development";

    [ObservableProperty]
    public partial bool ModeIsProduction { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<string> ModeLines { get; set; } = DevelopmentLines;

    [ObservableProperty]
    public partial bool HasFindings { get; set; }

    public string StartStopLabel => ApacheRunning ? "Stop" : "Start";

    public ObservableCollection<FindingItem> Findings { get; } = [];

    /// <summary>Raised by the mode card's button; the shell navigates.</summary>
    public event EventHandler? CompareProfilesRequested;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        _settings = await _api.GetSettingsAsync(ct);
        ApplySettings();
        Apply(await _api.GetStatusAsync(ct));
    }

    [RelayCommand]
    private Task StartStopApacheAsync() =>
        RunAsync(() => ApacheRunning ? _api.StopAsync(ServiceId.Apache) : _api.StartAsync(ServiceId.Apache));

    [RelayCommand]
    private Task RestartApacheAsync() => RunAsync(() => _api.RestartAsync(ServiceId.Apache));

    [RelayCommand]
    private void OpenHttpdConf() => _editor.Open(_paths.HttpdConf);

    [RelayCommand]
    private void OpenUrl(string url) => EditorLauncher.OpenUrl(url);

    [RelayCommand]
    private void CompareProfiles() => CompareProfilesRequested?.Invoke(this, EventArgs.Empty);

    private async Task RunAsync(Func<Task<ApplyResult>> call)
    {
        try
        {
            var result = await call();
            ApacheMessage = result.Ok ? null : string.Join("\n", new[] { result.Error, result.ConfigTestOutput }.Where(t => !string.IsNullOrWhiteSpace(t)));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Agent call failed");
            ApacheMessage = $"The ArbiPache Agent did not answer: {e.Message}";
        }
    }

    private async Task ReloadSettingsAsync()
    {
        try
        {
            var settings = await _api.GetSettingsAsync();
            _ui(() =>
            {
                _settings = settings;
                ApplySettings();
            });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Could not read settings from the Agent");
        }
    }

    private void ApplySettings()
    {
        var s = _settings;
        ApacheWorkers = $"WinNT MPM · {s.Apache.ThreadsPerChild} threads";
        ApachePorts = s.HttpPort == s.HttpsPort ? $"{s.HttpPort}" : $"{s.HttpPort} · {s.HttpsPort}";
        var port = s.HttpPort == 80 ? "" : $":{s.HttpPort}";
        LocalUrl = $"http://localhost{port}/";
        LocalNote = $"htdocs · {s.HtdocsPath ?? _paths.Htdocs}";
        NetworkUrl = s.Network.HtdocsOnNetwork && LanAddress.Find() is { } ip ? $"http://{ip}{port}/" : null;
        NetworkNote = s.Network.PrivateNetworksOnly ? "Your network · Private networks only" : "Your network";
    }

    private void Apply(AgentStatus status)
    {
        var apache = status.Services.FirstOrDefault(s => s.Id == ServiceId.Apache);
        var state = apache?.State ?? ProcessState.Stopped;
        ApacheRunning = state is ProcessState.Starting or ProcessState.Running or ProcessState.Restarting;
        ApacheFailed = state == ProcessState.Failed;
        ApacheVersion = apache?.Version ?? "Not installed";
        ApacheStateText = apache?.Version is null ? "Not installed" : state.ToString();
        if (ApacheFailed && apache?.Detail is { } detail) ApacheMessage = detail;

        ModeName = status.Mode.ToString();
        ModeIsProduction = status.Mode == ServerMode.Production;
        ModeLines = ModeIsProduction ? ProductionLines : DevelopmentLines;

        Findings.Clear();
        foreach (var f in status.Findings) Findings.Add(new FindingItem(f.Message, f.Fix, f.Severity == Severity.Blocking));
        HasFindings = Findings.Count > 0;
    }
}
