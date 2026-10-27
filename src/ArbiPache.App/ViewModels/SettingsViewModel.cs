using System.Security;
using System.Security.Cryptography;
using ArbiPache.App.Services;
using ArbiPache.Core;
using ArbiPache.Core.Agent;
using ArbiPache.Core.Elevation;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Settings;
using ArbiPache.Core.Sites;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace ArbiPache.App.ViewModels;

/// <summary>
/// The Settings page: startup, network access, ports, folders, the local certificate authority and the import from
/// ArbiPache 3. Every change goes through the Agent; a refused change puts the controls back as they were.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public const string AutomaticEditor = "Automatic: VS Code, then Notepad++, then Notepad";

    private readonly IAgentApi _api;
    private readonly IRunKey _runKey;
    private readonly ILocalCaTrust _ca;
    private readonly IFolderPicker _picker;
    private readonly AppPaths _paths;
    private readonly Func<string?> _lanIp;
    private readonly Action<AppSettings>? _settingsChanged;
    private readonly Action? _sitesChanged;
    private readonly ElevationFollowUp _followUp;
    private AppSettings _settings = new();

    /// <param name="settingsChanged">Told about every saved change, so the rest of the window follows.</param>
    /// <param name="sitesChanged">Told when imported sites or a new htdocs folder change the Sites page.</param>
    public SettingsViewModel(IAgentApi api, IElevator elevator, IRunKey runKey, ILocalCaTrust ca, IFolderPicker picker, AppPaths paths,
        Func<string?> lanIp, Action<AppSettings>? settingsChanged = null, Action? sitesChanged = null)
    {
        _api = api;
        _runKey = runKey;
        _ca = ca;
        _picker = picker;
        _paths = paths;
        _lanIp = lanIp;
        _settingsChanged = settingsChanged;
        _sitesChanged = sitesChanged;
        _followUp = new ElevationFollowUp(elevator);
    }

    public string AppFolder => _paths.Root;
    public string DataFolder => _paths.Data;
    public string VersionText => $"ArbiPache {BuildInfo.Version}";
    public string NetworkLabel => $"Let devices on my network open htdocs at {_lanIp() ?? "this PC's address"}";

    [ObservableProperty]
    public partial bool StartServicesOnLaunch { get; set; }

    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    [ObservableProperty]
    public partial bool RunAsService { get; set; }

    [ObservableProperty]
    public partial string EditorText { get; set; } = AutomaticEditor;

    [ObservableProperty]
    public partial bool HtdocsOnNetwork { get; set; }

    [ObservableProperty]
    public partial bool PrivateNetworksOnly { get; set; }

    [ObservableProperty]
    public partial string HttpPortText { get; set; } = "";

    [ObservableProperty]
    public partial string HttpsPortText { get; set; } = "";

    [ObservableProperty]
    public partial string? PortsError { get; set; }

    [ObservableProperty]
    public partial string HtdocsFolder { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaStatus))]
    public partial bool CaTrusted { get; set; }

    public string CaStatus => CaTrusted ? "Trusted" : "Not trusted";

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial bool MessageOk { get; set; }

    [ObservableProperty]
    public partial string? ImportMessage { get; set; }

    private string RunCommand => $"\"{Path.Combine(_paths.Root, "ArbiPache.exe")}\" --minimized";

    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            _settings = await _api.GetSettingsAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Loading settings failed");
            Say($"The ArbiPache Agent did not answer: {e.Message}", ok: false);
            return;
        }

        Show(_settings);
        CaTrusted = IsCaTrusted();
        // A portable folder may have moved since the Run entry was written.
        if (_settings.StartWithWindows) TryRunKey(set: true);
    }

    [RelayCommand]
    private async Task ApplyGeneralAsync()
    {
        var next = _settings with { StartServicesOnLaunch = StartServicesOnLaunch, StartWithWindows = StartWithWindows, CloseToTray = CloseToTray };
        if (!await SaveAsync(next)) return;
        TryRunKey(set: StartWithWindows);
    }

    /// <summary>Saves who may open htdocs and, when the network may, asks Windows to open both ports in the firewall.</summary>
    [RelayCommand]
    private async Task ApplyNetworkAsync()
    {
        var next = _settings with { Network = new NetworkAccess(HtdocsOnNetwork, PrivateNetworksOnly) };
        if (!await SaveAsync(next)) return;
        if (HtdocsOnNetwork) await OpenFirewallAsync();
    }

    [RelayCommand]
    private async Task ApplyPortsAsync()
    {
        if (!int.TryParse(HttpPortText.Trim(), out var http) || !int.TryParse(HttpsPortText.Trim(), out var https)
            || http is < 1 or > 65535 || https is < 1 or > 65535)
        {
            PortsError = "Ports are whole numbers from 1 to 65535.";
            return;
        }
        if (http == https)
        {
            PortsError = "HTTP and HTTPS need different ports.";
            return;
        }

        PortsError = null;
        if (!await SaveAsync(_settings with { HttpPort = http, HttpsPort = https })) return;
        if (_settings.Network.HtdocsOnNetwork) await OpenFirewallAsync();
    }

    [RelayCommand]
    private async Task ChangeHtdocsAsync()
    {
        if (await _picker.PickFolderAsync("Choose the htdocs folder") is not { } folder) return;
        if (await SaveAsync(_settings with { HtdocsPath = folder })) _sitesChanged?.Invoke();
    }

    [RelayCommand]
    private async Task ChooseEditorAsync()
    {
        if (await _picker.PickProgramAsync("Choose the program that opens config files") is not { } program) return;
        await SaveAsync(_settings with { Editor = program });
    }

    [RelayCommand]
    private async Task UseAutomaticEditorAsync() => await SaveAsync(_settings with { Editor = "auto" });

    [RelayCommand]
    private void OpenAppFolder() => EditorLauncher.OpenFolder(AppFolder);

    [RelayCommand]
    private void OpenHtdocsFolder() => EditorLauncher.OpenFolder(HtdocsFolder);

    [RelayCommand]
    private void OpenDataFolder() => EditorLauncher.OpenFolder(DataFolder);

    [RelayCommand]
    private void TrustCa()
    {
        try
        {
            _ca.Trust();
            Say("Windows now trusts ArbiPache's certificate authority. Restart the browser to see the padlock.", ok: true);
        }
        catch (Exception e) when (e is CryptographicException or InvalidOperationException)
        {
            Say($"The certificate authority was not trusted: {e.Message}", ok: false);
        }
        CaTrusted = IsCaTrusted();
    }

    [RelayCommand]
    private void UntrustCa()
    {
        try
        {
            _ca.Untrust();
            Say("Removed from Windows. Local development sites show a warning until you trust it again.", ok: true);
        }
        catch (CryptographicException e)
        {
            Say($"The certificate authority was not removed: {e.Message}", ok: false);
        }
        CaTrusted = IsCaTrusted();
    }

    [RelayCommand]
    private async Task ReissueAsync()
    {
        try
        {
            var result = await _api.ReissueLocalCertificatesAsync();
            Say(result.Ok ? "Local development certificates issued again." : Describe(result), result.Ok);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Say($"The ArbiPache Agent did not answer: {e.Message}", ok: false);
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        ImportReport report;
        try
        {
            report = await _api.ImportLegacyAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ImportMessage = $"The ArbiPache Agent did not answer: {e.Message}";
            return;
        }

        var lines = new List<string>();
        if (report.Result is { Ok: false } failed) lines.Add(Describe(failed));
        else if (report.Imported.Count > 0)
            lines.Add($"Imported {report.Imported.Count} site{(report.Imported.Count == 1 ? "" : "s")}: {string.Join(", ", report.Imported)}.");
        else if (report.Skipped.Count == 0) lines.Add("ArbiPache 3 has no sites to import.");
        lines.AddRange(report.Skipped.Select(s => $"Skipped {s}."));
        if (report.MissingPhpVersions.Count > 0)
            lines.Add($"Install PHP {string.Join(", ", report.MissingPhpVersions)} on the PHP page, then turn on the sites that use it.");
        if (report.Result is { Ok: true } applied && await _followUp.RunAsync(applied, _settings.HttpPort, _settings.HttpsPort) is { } problem)
            lines.Add(problem);
        ImportMessage = string.Join("\n", lines);
        if (report.Imported.Count > 0) _sitesChanged?.Invoke();
    }

    private async Task<bool> SaveAsync(AppSettings next)
    {
        ApplyResult result;
        try
        {
            result = await _api.SaveSettingsAsync(next);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Saving settings failed");
            result = ApplyResult.Failure($"The ArbiPache Agent did not answer: {e.Message}");
        }

        if (!result.Ok)
        {
            Show(_settings);
            Say(Describe(result), ok: false);
            return false;
        }

        _settings = next;
        Show(next);
        _settingsChanged?.Invoke(next);
        Say("Saved.", ok: true);
        return true;
    }

    private async Task OpenFirewallAsync()
    {
        var scope = _settings.Network.PrivateNetworksOnly ? FirewallScope.LocalNetwork : FirewallScope.Internet;
        if (await _followUp.FirewallAsync(scope, _settings.HttpPort, _settings.HttpsPort) is { } problem) Say(problem, ok: false);
    }

    private void Show(AppSettings s)
    {
        StartServicesOnLaunch = s.StartServicesOnLaunch;
        StartWithWindows = s.StartWithWindows;
        CloseToTray = s.CloseToTray;
        RunAsService = s.RunAsService;
        EditorText = string.Equals(s.Editor, "auto", StringComparison.OrdinalIgnoreCase) ? AutomaticEditor : s.Editor;
        HtdocsOnNetwork = s.Network.HtdocsOnNetwork;
        PrivateNetworksOnly = s.Network.PrivateNetworksOnly;
        HttpPortText = s.HttpPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        HttpsPortText = s.HttpsPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        HtdocsFolder = s.HtdocsPath ?? _paths.Htdocs;
    }

    private void TryRunKey(bool set)
    {
        try
        {
            if (set) _runKey.Set(RunCommand);
            else _runKey.Remove();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or SecurityException)
        {
            Log.Warning(e, "Writing the Run key failed");
            Say($"Windows did not let ArbiPache change its startup entry: {e.Message}", ok: false);
        }
    }

    private bool IsCaTrusted()
    {
        try
        {
            return _ca.IsTrusted();
        }
        catch (CryptographicException e)
        {
            Log.Warning(e, "Reading the certificate store failed");
            return false;
        }
    }

    private void Say(string text, bool ok)
    {
        Message = text;
        MessageOk = ok;
    }

    private static string Describe(ApplyResult r) =>
        string.Join("\n", new[] { r.Error, r.ConfigTestOutput?.Trim() }.Where(t => !string.IsNullOrWhiteSpace(t)));
}
