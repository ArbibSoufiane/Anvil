using System.Collections.ObjectModel;
using Anvil.App.Services;
using Anvil.Core.Agent;
using Anvil.Core.Components;
using Anvil.Core.Paths;
using Anvil.Core.Php;
using Anvil.Core.Processes;
using Anvil.Core.Settings;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace Anvil.App.ViewModels;

/// <summary>One installed PHP version in the versions table.</summary>
public sealed partial class PhpVersionRow : ObservableObject
{
    public required PhpVersionInfo Info { get; init; }
    public required string IniPath { get; init; }
    public required string ExtPath { get; init; }
    public required IAsyncRelayCommand SelectCommand { get; init; }

    public string Version => Info.Version;
    public string Title => $"PHP {Info.Version}";
    public string Build => BuildText(Info.ThreadSafe, Info.Toolset);
    public string PoolText => $"{Info.Workers} workers";
    public string UsedBy => Info.UsedBy.Count == 0 ? "none" : string.Join(" + ", Info.UsedBy);
    public string StatusText => Info.State.ToString();
    public bool IsRunning => Info.State == ProcessState.Running;

    public IReadOnlyList<string> Tags =>
        new[] { Info.IsDefault ? "Default" : null, Info.Linked ? "Linked" : null, Info.EndOfLife ? "End of life" : null }.OfType<string>().ToList();

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public static string BuildText(bool threadSafe, string toolset)
    {
        var build = threadSafe ? "TS x64" : "NTS x64";
        return toolset.Length == 0 ? build : $"{build} · {toolset.ToUpperInvariant()}";
    }
}

/// <summary>A PHP version pvm has that Anvil does not, with the button that adds it.</summary>
public sealed class PvmChoice(PhpFound found, Func<string, Task> add)
{
    public PhpFound Found { get; } = found;
    public string Label { get; } = $"PHP {found.Version} · {PhpVersionRow.BuildText(found.ThreadSafe, found.Toolset)}";
    public IAsyncRelayCommand AddCommand { get; } = new AsyncRelayCommand(() => add(found.Folder));
}

/// <summary>An extension chip.</summary>
public sealed partial class ExtensionChip : ObservableObject
{
    public ExtensionChip(string name, bool isOn, string? hint, Func<ExtensionChip, Task> toggle)
    {
        Name = name;
        IsOn = isOn;
        Hint = hint;
        ToggleCommand = new AsyncRelayCommand(() => toggle(this));
    }

    public string Name { get; }
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
    public const string XdebugHint = "Xdebug starts on a trigger: add XDEBUG_TRIGGER to a request, or use a browser extension.";

    private readonly IAgentApi _api;
    private readonly AppPaths _paths;
    private readonly EditorLauncher _editor;
    private readonly IFolderPicker? _picker;
    private readonly Action<Action> _ui;
    private IReadOnlyList<InstalledComponent> _xdebug = [];
    private IReadOnlyList<ComponentRelease> _xdebugAvailable = [];

    public PhpViewModel(IAgentApi api, AppPaths paths, EditorLauncher editor, IFolderPicker? picker = null, Action<Action>? ui = null)
    {
        _api = api;
        _paths = paths;
        _editor = editor;
        _picker = picker;
        _ui = ui ?? (a => Dispatcher.UIThread.Post(a));
        Updates = new UpdatesPanel(api, k => k == ComponentKind.Php, LoadAsync);
        var cores = Environment.ProcessorCount;
        WorkersHint = $"Suggested: half your cores ({Suggest.PhpWorkers(cores)} of {cores})";
        MaxExecutionTime = DevelopmentDefaults.Php["max_execution_time"];
        MaxExecutionTimeHint = "In a new php.ini. Edit php.ini to change it.";
    }

    public ObservableCollection<PhpVersionRow> Versions { get; } = [];
    public ObservableCollection<ExtensionChip> Extensions { get; } = [];
    public ObservableCollection<InstallChoice> InstallChoices { get; } = [];
    public ObservableCollection<PvmChoice> PvmChoices { get; } = [];
    public string WorkersHint { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(PoolTitle), nameof(ExtensionsTitle), nameof(CanUnlink))]
    [NotifyCanExecuteChangedFor(nameof(MakeDefaultCommand), nameof(ApplyPoolCommand), nameof(UnlinkCommand))]
    public partial PhpVersionRow? Selected { get; set; }

    public bool HasSelection => Selected is not null;

    /// <summary>The selected version was added from a folder, so Anvil can let go of it.</summary>
    public bool CanUnlink => Selected is { Info.Linked: true };
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
    public partial bool HasPvmChoices { get; set; }

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
            await RefreshVersionsAsync(Selected?.Version, ct);
            await RefreshInstallChoicesAsync(ct);
            await RefreshPvmChoicesAsync(ct);
            await Updates.RefreshAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Loading the PHP page failed");
            VersionsMessage = $"The Anvil Agent did not answer: {e.Message}";
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
            ExtensionsMessage = $"Xdebug {release.Version} installed and turned on for PHP {minor}.";
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
    private async Task AddFolderAsync()
    {
        if (_picker is null || await _picker.PickFolderAsync("Choose a PHP folder (the one with php-cgi.exe)") is not { } folder) return;
        await LinkAsync(folder);
    }

    private async Task LinkAsync(string folder)
    {
        VersionsMessage = null;
        var before = Versions.Select(v => v.Version).ToHashSet();
        ApplyResult result;
        try
        {
            result = await _api.LinkPhpAsync(folder);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            VersionsMessage = $"The Anvil Agent did not answer: {e.Message}";
            return;
        }
        if (!result.Ok)
        {
            VersionsMessage = result.Error;
            return;
        }
        await RefreshVersionsAsync(null, default);
        var added = Versions.FirstOrDefault(v => !before.Contains(v.Version));
        if (added is not null) await SelectAsync(added, default);
        await RefreshInstallChoicesAsync(default);
        await RefreshPvmChoicesAsync(default);
        VersionsMessage = added is null
            ? "Added. Anvil keeps its own php.ini for it and leaves the folder as it is."
            : $"PHP {added.Version} added from its folder. Anvil keeps its own php.ini for it and leaves the folder as it is.";
    }

    [RelayCommand(CanExecute = nameof(CanUnlink))]
    private async Task UnlinkAsync()
    {
        var row = Selected!;
        var result = await CallAsync(() => _api.UnlinkPhpAsync(row.Version));
        if (result is null) return;
        if (result.Ok)
        {
            await RefreshVersionsAsync(null, default);
            await RefreshInstallChoicesAsync(default);
            await RefreshPvmChoicesAsync(default);
        }
        ResultOk = result.Ok;
        ResultText = result.Ok ? $"PHP {row.Version} is no longer in Anvil. Its folder {row.Info.Folder} is unchanged." : result.Error;
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
                ExtPath = Path.Combine(info.Folder.Length > 0 ? info.Folder : _paths.PhpRuntime(info.Version), "ext"),
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
            ExtensionsMessage = $"The Anvil Agent did not answer: {e.Message}";
            return;
        }
        if (!ReferenceEquals(Selected, row)) return;

        var minor = PhpSupport.Minor(row.Version);
        var hasXdebug = _xdebug.Any(x => x.AppliesTo == minor);
        Extensions.Clear();
        foreach (var e in list.Where(e => e.Name != "xdebug"))
            Extensions.Add(new ExtensionChip(e.Name, e.Enabled, hint: null, ToggleAsync));
        if (hasXdebug)
            Extensions.Add(new ExtensionChip("xdebug", list.Any(e => e.Name == "xdebug" && e.Enabled), XdebugHint, ToggleAsync));
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

    private async Task RefreshPvmChoicesAsync(CancellationToken ct)
    {
        var found = await _api.FindPvmPhpAsync(ct);
        PvmChoices.Clear();
        foreach (var f in found.Where(f => !f.InAnvil)) PvmChoices.Add(new PvmChoice(f, LinkAsync));
        HasPvmChoices = PvmChoices.Count > 0;
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
}
