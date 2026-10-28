using System.Security;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Anvil.App.Services;
using Anvil.Core;
using Anvil.Core.Agent;
using Anvil.Core.Elevation;
using Anvil.Core.Paths;
using Anvil.Core.Settings;
using Anvil.Core.Sites;
using Anvil.Core.Update;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace Anvil.App.ViewModels;

/// <summary>
/// The Settings page: startup, network access, ports, folders, the local certificate authority and the import from
/// ArbiPache 3, and Anvil's own update. Every change goes through the Agent; a refused change puts the controls back as they were.
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
    private readonly IAppUpdate? _update;
    private readonly EditorChoice _editor;
    private AppSettings _settings = new();

    /// <param name="settingsChanged">Told about every saved change, so the rest of the window follows.</param>
    /// <param name="sitesChanged">Told when imported sites or a new htdocs folder change the Sites page.</param>
    /// <param name="update">Checks for and installs a newer Anvil; null hides the update buttons.</param>
    public SettingsViewModel(IAgentApi api, IElevator elevator, IRunKey runKey, ILocalCaTrust ca, IFolderPicker picker, AppPaths paths,
        Func<string?> lanIp, Action<AppSettings>? settingsChanged = null, Action? sitesChanged = null,
        IAppUpdate? update = null, EditorChoice? editor = null)
    {
        _update = update;
        _editor = editor ?? new EditorChoice(null);
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
    public string VersionText => $"Anvil {BuildInfo.Version}";
    public string NetworkLabel => $"Let devices on my network open htdocs at {_lanIp() ?? "this PC's address"}";

    [ObservableProperty]
    public partial bool StartServicesOnLaunch { get; set; }

    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

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

    [ObservableProperty]
    public partial string AcmeEmail { get; set; } = "";

    [ObservableProperty]
    public partial bool AcmeStaging { get; set; }

    [ObservableProperty]
    public partial string? AcmeEmailError { get; set; }

    /// <summary>Saves the Let's Encrypt contact (optional) and whether orders go to Let's Encrypt's staging server.</summary>
    [RelayCommand]
    private async Task ApplyCertificatesAsync()
    {
        var email = AcmeEmail.Trim();
        if (email.Length > 0 && !EmailAddress().IsMatch(email))
        {
            AcmeEmailError = "Enter an email address such as ops@example.com, or leave it empty.";
            return;
        }

        AcmeEmailError = null;
        await SaveAsync(_settings with { AcmeEmail = email.Length > 0 ? email : null, AcmeStaging = AcmeStaging });
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailAddress();

    public bool CanUpdate => _update is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateAvailable), nameof(InstallUpdateText))]
    public partial Release? AvailableUpdate { get; set; }

    public bool UpdateAvailable => AvailableUpdate is not null;
    public string InstallUpdateText => $"Install {AvailableUpdate?.Version}";

    [ObservableProperty]
    public partial string? UpdateMessage { get; set; }

    [ObservableProperty]
    public partial bool UpdateOk { get; set; }

    [ObservableProperty]
    public partial string? UpdateNotes { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand), nameof(InstallUpdateCommand))]
    public partial bool UpdateBusy { get; set; }

    private bool CanStartUpdate => !UpdateBusy;

    /// <summary>Asks GitHub for a newer Anvil; only when the user clicks.</summary>
    [RelayCommand(CanExecute = nameof(CanStartUpdate))]
    private async Task CheckForUpdatesAsync()
    {
        if (_update is null) return;
        if (_update.CannotUpdate is { } reason)
        {
            SayUpdate(reason, ok: false);
            return;
        }

        UpdateBusy = true;
        SayUpdate("Checking for updates…", ok: true);
        try
        {
            AvailableUpdate = await _update.CheckAsync(CancellationToken.None);
            UpdateNotes = string.IsNullOrWhiteSpace(AvailableUpdate?.Notes) ? null : AvailableUpdate.Notes.Trim();
            if (AvailableUpdate is not { } next) SayUpdate($"Anvil {BuildInfo.Version} is the latest version.", ok: true);
            else
                SayUpdate($"Anvil {next.Version} is available. Installing it restarts Anvil: " + (_settings.StartServicesOnLaunch
                    ? "sites stop for a moment and start again."
                    : "sites stop and stay stopped until you start them, because \"Start services when Anvil opens\" is off."), ok: true);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Checking for an Anvil update failed");
            AvailableUpdate = null;
            UpdateNotes = null;
            SayUpdate($"Anvil could not check for updates: {e.Message}", ok: false);
        }
        finally
        {
            UpdateBusy = false;
        }
    }

    /// <summary>Downloads the offered release and hands over to it; the window closes and the new version opens.</summary>
    [RelayCommand(CanExecute = nameof(CanStartUpdate))]
    private async Task InstallUpdateAsync()
    {
        if (_update is null || AvailableUpdate is not { } next) return;
        UpdateBusy = true;
        SayUpdate($"Downloading Anvil {next.Version}…", ok: true);
        var progress = new Progress<long>(bytes =>
        {
            if (UpdateBusy) UpdateMessage = $"Downloading Anvil {next.Version}… {bytes / 1048576.0:0.0} MB";
        });
        var problem = await _update.InstallAsync(next, progress, CancellationToken.None);
        UpdateBusy = false;
        if (problem is null) SayUpdate($"Installing Anvil {next.Version}. It opens again in a moment.", ok: true);
        else SayUpdate(problem, ok: false);
    }

    private void SayUpdate(string text, bool ok)
    {
        UpdateMessage = text;
        UpdateOk = ok;
    }

    private string RunCommand => $"\"{Path.Combine(_paths.Root, "Anvil.exe")}\" --minimized";

    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            _settings = await _api.GetSettingsAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Loading settings failed");
            Say($"The Anvil Agent did not answer: {e.Message}", ok: false);
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
        UseEditor(program);
    }

    [RelayCommand]
    private void UseAutomaticEditor() => UseEditor("auto");

    private void UseEditor(string program)
    {
        _editor.Editor = program;
        EditorText = EditorLabel();
    }

    private string EditorLabel() =>
        _editor.Editor is var chosen && string.Equals(chosen, "auto", StringComparison.OrdinalIgnoreCase) ? AutomaticEditor : chosen;

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
            Say("Windows now trusts Anvil's certificate authority. Restart the browser to see the padlock.", ok: true);
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
            Say($"The Anvil Agent did not answer: {e.Message}", ok: false);
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
            ImportMessage = $"The Anvil Agent did not answer: {e.Message}";
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
            result = ApplyResult.Failure($"The Anvil Agent did not answer: {e.Message}");
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
        EditorText = EditorLabel();
        HtdocsOnNetwork = s.Network.HtdocsOnNetwork;
        PrivateNetworksOnly = s.Network.PrivateNetworksOnly;
        HttpPortText = s.HttpPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        HttpsPortText = s.HttpsPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        HtdocsFolder = s.HtdocsPath ?? _paths.Htdocs;
        AcmeEmail = s.AcmeEmail ?? "";
        AcmeStaging = s.AcmeStaging;
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
            Say($"Windows did not let Anvil change its startup entry: {e.Message}", ok: false);
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
