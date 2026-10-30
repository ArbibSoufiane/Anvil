using System.Collections.ObjectModel;
using Anvil.App.Services;
using Anvil.Core.Agent;
using Anvil.Core.Elevation;
using Anvil.Core.Network;
using Anvil.Core.Paths;
using Anvil.Core.Preflight;
using Anvil.Core.Processes;
using Anvil.Core.Settings;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace Anvil.App.ViewModels;

/// <summary>A problem on the Dashboard; ActionLabel names the button that deals with it, when Anvil can.</summary>
public sealed record FindingItem(string Id, string Message, string? Fix, bool Blocking)
{
    public string? ActionLabel => Id switch
    {
        NetworkFindings.FirewallId => "Open firewall",
        NetworkFindings.PublicId => "Open network settings",
        _ => null,
    };

    public bool HasAction => ActionLabel is not null;

    /// <summary>A blocking problem stays on the Dashboard: Apache does not start until it is dealt with.</summary>
    public bool CanDismiss => !Blocking;

    /// <summary>The same problem with other details, such as another port, is a new warning.</summary>
    public string Key => KeyOf(Id, Message);

    public static string KeyOf(string id, string message) => $"{id}\n{message}";
}

/// <summary>The Dashboard: the Apache and PHP cards, problems found before start, where to open the sites, and the development settings.</summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    /// <summary>What a new php.ini starts with, and what Apache always runs with.</summary>
    public static IReadOnlyList<string> DevelopmentLines { get; } =
    [
        "PHP errors shown in the page, every notice reported",
        "Scripts may run 300 s, 1 GB memory, 256 MB uploads",
        "OPcache re-checks files on every request",
        "Xdebug on once installed, directory listings on",
    ];

    private readonly IAgentApi _api;
    private readonly AppPaths _paths;
    private readonly EditorLauncher _editor;
    private readonly Action<Action> _ui;
    private readonly ElevationFollowUp? _followUp;
    private readonly Action<string> _openUrl;
    private AppSettings _settings = new();
    private IReadOnlyList<PreflightFinding> _lastFindings = [];
    private readonly HashSet<string> _dismissed = [];

    /// <param name="elevator">Asks Windows for the firewall rules from the firewall banner; null only has the Agent look again.</param>
    /// <param name="openUrl">Opens a URL or a Windows settings page; null starts it through the shell.</param>
    public DashboardViewModel(IAgentApi api, AppPaths paths, EditorLauncher editor, Action<Action>? ui = null, IElevator? elevator = null,
        Action<string>? openUrl = null)
    {
        _api = api;
        _paths = paths;
        _editor = editor;
        _ui = ui ?? (a => Dispatcher.UIThread.Post(a));
        _followUp = elevator is null ? null : new ElevationFollowUp(elevator);
        _openUrl = openUrl ?? EditorLauncher.OpenUrl;
        api.StatusChanged += (_, s) =>
        {
            _ui(() => Apply(s));
            _ = ReloadSettingsAsync();
        };
        ApplySettings();
        Apply(new AgentStatus([], [], "", []));
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

    [ObservableProperty]
    public partial string PhpStateText { get; set; } = "Not installed";

    [ObservableProperty]
    public partial bool PhpRunning { get; set; }

    [ObservableProperty]
    public partial bool PhpFailed { get; set; }

    [ObservableProperty]
    public partial string PhpDefault { get; set; } = "Install PHP on the PHP page";

    [ObservableProperty]
    public partial string PhpPools { get; set; } = "";

    private string? _defaultPhp;
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
    public partial bool HasFindings { get; set; }

    /// <summary>What went wrong when a finding's button was pressed.</summary>
    [ObservableProperty]
    public partial string? FindingMessage { get; set; }

    public string StartStopLabel => ApacheRunning ? "Stop" : "Start";

    public ObservableCollection<FindingItem> Findings { get; } = [];

    public async Task LoadAsync(CancellationToken ct = default)
    {
        _settings = await _api.GetSettingsAsync(ct);
        ApplySettings();
        Apply(await _api.GetStatusAsync(ct));
    }

    [RelayCommand]
    private Task StartStopApacheAsync() =>
        RunAsync(() => ApacheRunning ? _api.StopAsync(ServiceId.Apache) : _api.StartAsync(ServiceId.Apache), m => ApacheMessage = m);

    [RelayCommand]
    private Task RestartApacheAsync() => RunAsync(() => _api.RestartAsync(ServiceId.Apache), m => ApacheMessage = m);

    [RelayCommand]
    private Task RestartPhpAsync() => RunAsync(() => _api.RestartAsync(ServiceId.Php), m => ApacheMessage = m);

    [RelayCommand]
    private void OpenHttpdConf() => _editor.Open(_paths.HttpdConf);

    [RelayCommand]
    private void OpenPhpIni()
    {
        if (_defaultPhp is { } version) _editor.Open(Path.Combine(_paths.PhpConfigDir(version), "php.ini"));
    }

    [RelayCommand]
    private void OpenUrl(string url) => _openUrl(url);

    /// <summary>Has the Agent look at the networks and the firewall again; its status updates the banners.</summary>
    public async Task CheckNetworkAgainAsync()
    {
        try
        {
            await _api.RefreshNetworkAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The Dashboard already says when the Agent does not answer.
            Log.Debug(e, "The network check did not run");
        }
    }

    /// <summary>
    /// The firewall banner runs the Agent's firewall request through UAC, then has the Agent look again; the Public
    /// network banner opens Windows' network settings.
    /// </summary>
    [RelayCommand]
    private async Task RunFindingActionAsync(FindingItem finding)
    {
        if (finding.Id == NetworkFindings.PublicId)
        {
            _openUrl("ms-settings:network-status");
            return;
        }
        if (finding.Id != NetworkFindings.FirewallId) return;

        try
        {
            FindingMessage = _followUp is not null && await _api.GetFirewallRequestAsync() is { } request
                ? await _followUp.FirewallAsync(request)
                : null;
            await _api.RefreshNetworkAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Opening the firewall from the Dashboard failed");
            FindingMessage = $"The Anvil Agent did not answer: {e.Message}";
        }
    }

    private static async Task RunAsync(Func<Task<ApplyResult>> call, Action<string?> show)
    {
        try
        {
            var result = await call();
            show(result.Ok ? null : string.Join("\n", new[] { result.Error, result.ConfigTestOutput }.Where(t => !string.IsNullOrWhiteSpace(t))));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Agent call failed");
            show($"The Anvil Agent did not answer: {e.Message}");
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
        NetworkUrl = s.Network.HtdocsOnNetwork && LanAddresses.Find().FirstOrDefault() is { } lan ? $"http://{lan.Address}{port}/" : null;
        NetworkNote = "Your network · Private networks only";
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

        var php = status.Services.FirstOrDefault(s => s.Id == ServiceId.Php);
        var phpState = php?.State ?? ProcessState.Stopped;
        _defaultPhp = php?.Version;
        PhpRunning = phpState is ProcessState.Starting or ProcessState.Running or ProcessState.Restarting;
        PhpFailed = phpState == ProcessState.Failed;
        PhpStateText = _defaultPhp is null ? "Not installed" : phpState.ToString();
        PhpDefault = _defaultPhp is null ? "Install PHP on the PHP page" : $"PHP {_defaultPhp} NTS x64";
        PhpPools = php?.Detail ?? (_defaultPhp is null ? "" : "Start with Apache");

        _lastFindings = status.Findings;
        ShowFindings();
    }

    /// <summary>Hides the warning until the problem goes away and comes back, or until Anvil is opened again.</summary>
    [RelayCommand]
    private void DismissFinding(FindingItem finding)
    {
        _dismissed.Add(finding.Key);
        ShowFindings();
    }

    private void ShowFindings()
    {
        // A problem that went away may be shown again the next time it is found.
        _dismissed.IntersectWith(_lastFindings.Select(f => FindingItem.KeyOf(f.Id, f.Message)));
        Findings.Clear();
        foreach (var f in _lastFindings)
        {
            var item = new FindingItem(f.Id, f.Message, f.Fix, f.Severity == Severity.Blocking);
            if (!_dismissed.Contains(item.Key)) Findings.Add(item);
        }
        HasFindings = Findings.Count > 0;
    }
}
