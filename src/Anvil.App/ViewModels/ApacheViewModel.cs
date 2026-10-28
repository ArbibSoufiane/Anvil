using System.Collections.ObjectModel;
using Anvil.App.Services;
using Anvil.Core.Agent;
using Anvil.Core.Apache;
using Anvil.Core.Components;
using Anvil.Core.Paths;
using Anvil.Core.Processes;
using Anvil.Core.Settings;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace Anvil.App.ViewModels;

/// <summary>A module chip. Required modules are always on.</summary>
public sealed partial class ModuleChip : ObservableObject
{
    public ModuleChip(string name, bool isRequired, bool isOn)
    {
        Name = name;
        IsRequired = isRequired;
        IsOn = isOn;
        ToggleCommand = new RelayCommand(() => IsOn = !IsOn, () => !IsRequired);
    }

    public string Name { get; }
    public bool IsRequired { get; }

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    public IRelayCommand ToggleCommand { get; }
}

/// <summary>One Apache build: installed, available to download, or both.</summary>
public sealed partial class VersionRow : ObservableObject
{
    public required string Version { get; init; }
    public required string Title { get; init; }
    public required string Note { get; init; }
    public required bool IsActive { get; init; }
    public required bool IsInstalled { get; init; }
    public ComponentRelease? Release { get; init; }
    public required IAsyncRelayCommand ActionCommand { get; init; }

    public string ActionLabel => IsActive ? "Active" : IsInstalled ? "Switch" : "Install";
}

/// <param name="IsFolder">Open in Explorer instead of the editor.</param>
public sealed record ConfigFileRow(string Name, string Note, string ActionLabel, string Path, bool IsFolder);

/// <summary>The Apache page: versions, WinNT MPM tuning, modules, configuration files and the config test.</summary>
public sealed partial class ApacheViewModel : ObservableObject
{
    private readonly IAgentApi _api;
    private readonly EditorLauncher _editor;
    private readonly Action<Action> _ui;
    private AppSettings _settings = new();
    private bool _apacheRunning;

    public ApacheViewModel(IAgentApi api, AppPaths paths, EditorLauncher editor, Action<Action>? ui = null)
    {
        _api = api;
        _editor = editor;
        _ui = ui ?? (a => Dispatcher.UIThread.Post(a));
        Updates = new UpdatesPanel(api, k => k == ComponentKind.Apache, LoadAsync);
        var cores = Environment.ProcessorCount;
        ThreadsHint = $"{cores} cores detected · suggested {Suggest.Threads(cores)}";
        ConfigFiles =
        [
            new("httpd.conf", "Yours. Anvil adds one Include line.", "Open", paths.HttpdConf, false),
            new("anvil.conf", "Generated from your settings", "View", Path.Combine(paths.Conf, "anvil.conf"), false),
            new("defaults.conf", "Generated development defaults", "View", Path.Combine(paths.Conf, "defaults.conf"), false),
            new(@"vhosts\", "One generated file per domain site", "Open folder", paths.Vhosts, true),
        ];
        api.StatusChanged += (_, s) => _ui(() => _apacheRunning = IsRunning(s));
        ApplySettings();
    }

    public ObservableCollection<VersionRow> Versions { get; } = [];
    public ObservableCollection<ModuleChip> Modules { get; } = [];
    public IReadOnlyList<ConfigFileRow> ConfigFiles { get; }
    public string ThreadsHint { get; }

    [ObservableProperty]
    public partial int ThreadsPerChild { get; set; }

    [ObservableProperty]
    public partial int MaxConnectionsPerChild { get; set; }

    [ObservableProperty]
    public partial int KeepAliveTimeout { get; set; }

    [ObservableProperty]
    public partial string RequestTimeout { get; set; } = "";

    [ObservableProperty]
    public partial string RequestTimeoutHint { get; set; } = "";

    [ObservableProperty]
    public partial string? ResultText { get; set; }

    [ObservableProperty]
    public partial bool ResultOk { get; set; }

    [ObservableProperty]
    public partial string? VersionsMessage { get; set; }

    [ObservableProperty]
    public partial string? TestText { get; set; }

    [ObservableProperty]
    public partial bool TestOk { get; set; }

    public UpdatesPanel Updates { get; }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        _settings = await _api.GetSettingsAsync(ct);
        _apacheRunning = IsRunning(await _api.GetStatusAsync(ct));
        ApplySettings();
        await RefreshVersionsAsync(ct);
        await Updates.RefreshAsync(ct);
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        var next = _settings with
        {
            Apache = new ApacheTuning(ThreadsPerChild, MaxConnectionsPerChild, KeepAliveTimeout),
            OptionalModules = Modules.Where(m => !m.IsRequired && m.IsOn).Select(m => m.Name).ToList(),
        };
        var result = await CallAsync(() => _api.SaveSettingsAsync(next));
        if (result is null) return;
        ResultOk = result.Ok;
        if (result.Ok)
        {
            _settings = next;
            ResultText = _apacheRunning ? "Saved. Apache reloaded with the new configuration." : "Saved. Apache uses it the next time it starts.";
        }
        else
        {
            ResultText = Describe(result);
            ApplySettings();
        }
    }

    [RelayCommand]
    private async Task TestConfigAsync()
    {
        try
        {
            var result = await _api.TestConfigAsync();
            TestOk = result.Ok;
            TestText = result.Output.Trim();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            TestOk = false;
            TestText = $"The Anvil Agent did not answer: {e.Message}";
        }
    }

    [RelayCommand]
    private void OpenConfigFile(ConfigFileRow row)
    {
        if (row.IsFolder) EditorLauncher.OpenFolder(row.Path);
        else _editor.Open(row.Path);
    }

    private async Task RunVersionActionAsync(VersionRow row)
    {
        VersionsMessage = null;
        if (!row.IsInstalled && row.Release is { } release)
        {
            VersionsMessage = $"Downloading Apache {row.Version}…";
            try
            {
                await _api.InstallAsync(release);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                VersionsMessage = $"Apache {row.Version} was not installed: {e.Message}";
                return;
            }
            VersionsMessage = null;
            if (_settings.ActiveApache is not null)
            {
                await RefreshVersionsAsync(default);
                return;
            }
        }

        var activated = await CallAsync(() => _api.ActivateAsync(ComponentKind.Apache, row.Version));
        if (activated is { Ok: false }) VersionsMessage = Describe(activated);
        _settings = await _api.GetSettingsAsync();
        await RefreshVersionsAsync(default);
    }

    private async Task RefreshVersionsAsync(CancellationToken ct)
    {
        IReadOnlyList<InstalledComponent> installed;
        IReadOnlyList<ComponentRelease> available;
        try
        {
            installed = await _api.GetInstalledAsync(ComponentKind.Apache, ct);
            available = await _api.GetAvailableAsync(ComponentKind.Apache, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            VersionsMessage = $"The Anvil Agent did not answer: {e.Message}";
            return;
        }

        var versions = installed.Select(i => i.Version).Union(available.Select(r => r.Version)).OrderByDescending(v => v, Comparer<string>.Create(SemVer.Compare));
        Versions.Clear();
        foreach (var version in versions)
        {
            var have = installed.FirstOrDefault(i => i.Version == version);
            var release = available.FirstOrDefault(r => r.Version == version);
            var toolset = release?.Toolset is { } t ? $" · {t}" : "";
            VersionRow? row = null;
            row = new VersionRow
            {
                Version = version,
                Title = $"Apache {version}{toolset}",
                Note = have is not null ? $"Installed {have.InstalledAt:MMM yyyy} · from Apache Lounge" : "Available from Apache Lounge",
                IsActive = version == _settings.ActiveApache,
                IsInstalled = have is not null,
                Release = release,
                ActionCommand = new AsyncRelayCommand(() => RunVersionActionAsync(row!), () => version != _settings.ActiveApache),
            };
            Versions.Add(row);
        }
    }

    private async Task<ApplyResult?> CallAsync(Func<Task<ApplyResult>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Agent call failed");
            ResultOk = false;
            ResultText = $"The Anvil Agent did not answer: {e.Message}";
            return null;
        }
    }

    private void ApplySettings()
    {
        var s = _settings;
        ThreadsPerChild = s.Apache.ThreadsPerChild;
        MaxConnectionsPerChild = s.Apache.MaxConnectionsPerChild;
        KeepAliveTimeout = s.Apache.KeepAliveTimeout;
        RequestTimeout = DevelopmentDefaults.Apache["Timeout"];
        RequestTimeoutHint = "Fixed for development";

        Modules.Clear();
        foreach (var name in ApacheModules.Required) Modules.Add(new ModuleChip(name, isRequired: true, isOn: true));
        foreach (var name in ApacheModules.Optional)
            Modules.Add(new ModuleChip(name, isRequired: false, s.OptionalModules.Contains(name, StringComparer.OrdinalIgnoreCase)));
    }

    private static bool IsRunning(AgentStatus s) =>
        s.Services.FirstOrDefault(x => x.Id == ServiceId.Apache)?.State is ProcessState.Starting or ProcessState.Running or ProcessState.Restarting;

    private static string Describe(ApplyResult r) =>
        string.Join("\n", new[] { r.Error, r.ConfigTestOutput?.Trim() }.Where(t => !string.IsNullOrWhiteSpace(t)));
}
