using System.Collections.ObjectModel;
using ArbiPache.App.Services;
using ArbiPache.Core.Agent;
using ArbiPache.Core.Components;
using ArbiPache.Core.Modes;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Php;
using ArbiPache.Core.Processes;
using ArbiPache.Core.Settings;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace ArbiPache.App.ViewModels;

/// <summary>One installed PHP version in the versions table.</summary>
public sealed partial class PhpVersionRow : ObservableObject
{
    public required PhpVersionInfo Info { get; init; }
    public required string IniPath { get; init; }
    public required string ExtPath { get; init; }
    public required IAsyncRelayCommand SelectCommand { get; init; }

    public string Version => Info.Version;
    public string Title => $"PHP {Info.Version}";
    public string Build => Info.Toolset.Length == 0 ? "NTS x64" : $"NTS x64 · {Info.Toolset.ToUpperInvariant()}";
    public string PoolText => $"{Info.Workers} workers";
    public string UsedBy => Info.UsedBy.Count == 0 ? "none" : string.Join(" + ", Info.UsedBy);
    public string StatusText => Info.State.ToString();
    public bool IsRunning => Info.State == ProcessState.Running;

    public IReadOnlyList<string> Tags =>
        new[] { Info.IsDefault ? "Default" : null, Info.EndOfLife ? "End of life" : null }.OfType<string>().ToList();

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>An extension chip. Xdebug is locked: the mode decides whether it loads.</summary>
public sealed partial class ExtensionChip : ObservableObject
{
    public ExtensionChip(string name, bool isOn, bool isLocked, string? hint, Func<ExtensionChip, Task> toggle)
    {
        Name = name;
        IsOn = isOn;
        IsLocked = isLocked;
        Hint = hint;
        ToggleCommand = new AsyncRelayCommand(() => toggle(this), () => !IsLocked);
    }

    public string Name { get; }
    public bool IsLocked { get; }
    public string? Hint { get; }

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    public IAsyncRelayCommand ToggleCommand { get; }
}

/// <summary>A PHP branch that is not installed yet, offered at its newest patch.</summary>
public sealed record InstallChoice(string Label, ComponentRelease Release);

/// <summary>The PHP page: installed versions, the selected version's pool and extensions, Xdebug and new versions.</summary>
public sealed partial class PhpViewModel : ObservableObject
{
    public const string XdebugHint = "Xdebug follows the mode: loaded in Development, not loaded in Production.";

    private readonly IAgentApi _api;
    private readonly AppPaths _paths;
    private readonly EditorLauncher _editor;
    private readonly Action<Action> _ui;
    private IReadOnlyList<InstalledComponent> _xdebug = [];
    private IReadOnlyList<ComponentRelease> _xdebugAvailable = [];

    public PhpViewModel(IAgentApi api, AppPaths paths, EditorLauncher editor, Action<Action>? ui = null)
    {
        _api = api;
        _paths = paths;
        _editor = editor;
        _ui = ui ?? (a => Dispatcher.UIThread.Post(a));
        Updates = new UpdatesPanel(api, k => k == ComponentKind.Php, LoadAsync);
        var cores = Environment.ProcessorCount;
        WorkersHint = $"Suggested: half your cores ({Suggest.PhpWorkers(cores)} of {cores})";
        api.StatusChanged += (_, s) => _ui(() => ApplyMode(s.Mode));
        ApplyMode(ServerMode.Development);
    }

    public ObservableCollection<PhpVersionRow> Versions { get; } = [];
    public ObservableCollection<ExtensionChip> Extensions { get; } = [];
    public ObservableCollection<InstallChoice> InstallChoices { get; } = [];
    public string WorkersHint { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(PoolTitle), nameof(ExtensionsTitle))]
    [NotifyCanExecuteChangedFor(nameof(MakeDefaultCommand), nameof(ApplyPoolCommand))]
    public partial PhpVersionRow? Selected { get; set; }

    public bool HasSelection => Selected is not null;
    public string PoolTitle => Selected is { } s ? $"PHP {s.Version} worker pool" : "Worker pool";
    public string ExtensionsTitle => Selected is { } s ? $"Extensions · PHP {s.Version}" : "Extensions";

    [ObservableProperty]
    public partial int Workers { get; set; }

    [ObservableProperty]
    public partial int MaxRequests { get; set; }

    [ObservableProperty]
    public partial string PortText { get; set; } = "";

    [ObservableProperty]
    public partial string MaxExecutionTime { get; set; } = "";

    [ObservableProperty]
    public partial string MaxExecutionTimeHint { get; set; } = "";

    [ObservableProperty]
    public partial string? ResultText { get; set; }

    [ObservableProperty]
    public partial bool ResultOk { get; set; }

    [ObservableProperty]
    public partial string? VersionsMessage { get; set; }

    [ObservableProperty]
    public partial bool HasVersions { get; set; }

    [ObservableProperty]
    public partial string? ExtensionsMessage { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GetXdebugCommand))]
    public partial bool CanGetXdebug { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial InstallChoice? SelectedInstall { get; set; }

    public UpdatesPanel Updates { get; }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            ApplyMode((await _api.GetStatusAsync(ct)).Mode);
            await RefreshVersionsAsync(Selected?.Version, ct);
            await RefreshInstallChoicesAsync(ct);
            await Updates.RefreshAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Loading the PHP page failed");
            VersionsMessage = $"The ArbiPache Agent did not answer: {e.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ApplyPoolAsync()
    {
        var row = Selected!;
        var result = await CallAsync(() => _api.SetPhpPoolAsync(row.Version, new PhpPoolSettings(Workers, MaxRequests)));
        if (result is null) return;
        ResultOk = result.Ok;
        ResultText = !result.Ok ? result.Error
            : row.IsRunning ? "Saved. The pool restarted its workers with the new settings."
            : "Saved. The pool uses them the next time it starts.";
        if (result.Ok) await RefreshVersionsAsync(row.Version, default);
    }

    private bool CanMakeDefault() => Selected is { Info.IsDefault: false };

    [RelayCommand(CanExecute = nameof(CanMakeDefault))]
    private async Task MakeDefaultAsync()
    {
        var row = Selected!;
        var result = await CallAsync(() => _api.SetDefaultPhpAsync(row.Version));
        if (result is null) return;
        ResultOk = result.Ok;
        ResultText = result.Ok
            ? $"PHP {row.Version} now runs htdocs."
            : string.Join("\n", new[] { result.Error, result.ConfigTestOutput?.Trim() }.Where(t => !string.IsNullOrWhiteSpace(t)));
        await RefreshVersionsAsync(row.Version, default);
    }

    [RelayCommand(CanExecute = nameof(CanGetXdebug))]
    private async Task GetXdebugAsync()
    {
        if (Selected is not { } row) return;
        var minor = PhpSupport.Minor(row.Version);
        if (_xdebugAvailable.FirstOrDefault(r => r.AppliesTo == minor) is not { } release)
        {
            ExtensionsMessage = $"The catalog has no Xdebug build for PHP {minor}.";
            return;
        }
        ExtensionsMessage = $"Downloading Xdebug {release.Version} for PHP {minor}…";
        try
        {
            await _api.InstallAsync(release);
            ExtensionsMessage = $"Xdebug {release.Version} installed. It loads in Development mode.";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ExtensionsMessage = $"Xdebug was not installed: {e.Message}";
        }
        await RefreshExtensionsAsync(row, default);
    }

    private bool CanInstall() => SelectedInstall is not null;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        var choice = SelectedInstall!;
        VersionsMessage = $"Downloading PHP {choice.Release.Version}…";
        try
        {
            await _api.InstallAsync(choice.Release);
            VersionsMessage = $"PHP {choice.Release.Version} installed.";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            VersionsMessage = $"PHP {choice.Release.Version} was not installed: {e.Message}";
            return;
        }
        await RefreshVersionsAsync(choice.Release.Version, default);
        await RefreshInstallChoicesAsync(default);
    }

    [RelayCommand]
    private void OpenIni(PhpVersionRow row) => _editor.Open(row.IniPath);

    [RelayCommand]
    private static void OpenExtFolder(PhpVersionRow row) => EditorLauncher.OpenFolder(row.ExtPath);

    private async Task SelectAsync(PhpVersionRow row, CancellationToken ct)
    {
        foreach (var r in Versions) r.IsSelected = ReferenceEquals(r, row);
        Selected = row;
        Workers = row.Info.Workers;
        MaxRequests = row.Info.MaxRequests;
        PortText = row.Info.Port == 0 ? "Starts with Apache" : $"127.0.0.1:{row.Info.Port}";
        ExtensionsMessage = null;
        await RefreshExtensionsAsync(row, ct);
    }

    private async Task RefreshVersionsAsync(string? keep, CancellationToken ct)
    {
        var php = await _api.GetPhpAsync(ct);
        _xdebug = await _api.GetInstalledAsync(ComponentKind.Xdebug, ct);
        Versions.Clear();
        foreach (var info in php)
        {
            PhpVersionRow? row = null;
            row = new PhpVersionRow
            {
                Info = info,
                IniPath = Path.Combine(_paths.PhpConfigDir(info.Version), "php.ini"),
                ExtPath = Path.Combine(_paths.PhpRuntime(info.Version), "ext"),
                SelectCommand = new AsyncRelayCommand(() => SelectAsync(row!, default)),
            };
            Versions.Add(row);
        }
        HasVersions = Versions.Count > 0;
        var next = Versions.FirstOrDefault(v => v.Version == keep) ?? Versions.FirstOrDefault(v => v.Info.IsDefault) ?? Versions.FirstOrDefault();
        if (next is null)
        {
            Selected = null;
            Extensions.Clear();
            CanGetXdebug = false;
            return;
        }
        await SelectAsync(next, ct);
    }

    private async Task RefreshExtensionsAsync(PhpVersionRow row, CancellationToken ct)
    {
        IReadOnlyList<PhpExtension> list;
        try
        {
            list = await _api.GetExtensionsAsync(row.Version, ct);
            _xdebug = await _api.GetInstalledAsync(ComponentKind.Xdebug, ct);
            _xdebugAvailable = await _api.GetAvailableAsync(ComponentKind.Xdebug, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ExtensionsMessage = $"The ArbiPache Agent did not answer: {e.Message}";
            return;
        }
        if (!ReferenceEquals(Selected, row)) return;

        var minor = PhpSupport.Minor(row.Version);
        var hasXdebug = _xdebug.Any(x => x.AppliesTo == minor);
        Extensions.Clear();
        foreach (var e in list.Where(e => e.Name != "xdebug"))
            Extensions.Add(new ExtensionChip(e.Name, e.Enabled, isLocked: false, hint: null, ToggleAsync));
        if (hasXdebug)
            Extensions.Add(new ExtensionChip("xdebug", list.Any(e => e.Name == "xdebug" && e.Enabled), isLocked: true, XdebugHint, ToggleAsync));
        CanGetXdebug = !hasXdebug && _xdebugAvailable.Any(r => r.AppliesTo == minor);
    }

    private async Task ToggleAsync(ExtensionChip chip)
    {
        if (Selected is not { } row) return;
        ExtensionsMessage = null;
        var result = await CallAsync(() => _api.SetExtensionAsync(row.Version, chip.Name, !chip.IsOn));
        if (result is null) return;
        if (result.Ok) chip.IsOn = !chip.IsOn;
        else ExtensionsMessage = result.Error;
    }

    private async Task RefreshInstallChoicesAsync(CancellationToken ct)
    {
        var available = await _api.GetAvailableAsync(ComponentKind.Php, ct);
        var installed = Versions.Select(v => PhpSupport.Minor(v.Version)).ToHashSet();
        var today = DateOnly.FromDateTime(DateTime.Today);
        InstallChoices.Clear();
        foreach (var branch in available.GroupBy(r => PhpSupport.Minor(r.Version)).Where(g => !installed.Contains(g.Key)))
        {
            var newest = branch.OrderByDescending(r => r.Version, Comparer<string>.Create(SemVer.Compare)).First();
            var eol = PhpSupport.IsEndOfLife(newest.Version, today) ? " · end of life" : "";
            InstallChoices.Add(new InstallChoice($"PHP {branch.Key} · {newest.Version}{eol}", newest));
        }
        SelectedInstall = InstallChoices.FirstOrDefault();
    }

    private void ApplyMode(ServerMode mode)
    {
        var profile = BuiltInProfiles.For(mode);
        MaxExecutionTime = profile.Php.TryGetValue("max_execution_time", out var value) ? value : "";
        MaxExecutionTimeHint = $"Set by {mode} profile";
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
            ResultText = $"The ArbiPache Agent did not answer: {e.Message}";
            return null;
        }
    }
}
