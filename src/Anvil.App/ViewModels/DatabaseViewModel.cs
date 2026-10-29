using System.Collections.ObjectModel;
using Anvil.App.Services;
using Anvil.Core.Agent;
using Anvil.Core.Components;
using Anvil.Core.Database;
using Anvil.Core.Paths;
using Anvil.Core.Processes;
using Anvil.Core.Settings;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace Anvil.App.ViewModels;

/// <summary>One row of the server settings table. An empty value means the server's default.</summary>
public sealed partial class DbSettingItem : ObservableObject
{
    private readonly Action _edited;

    public DbSettingItem(DbSettingRow row, Action edited)
    {
        Row = row;
        _edited = edited;
        Value = row.Current ?? "";
        Hint = row.InitOnly ? "Fixed when the database is created." : null;
    }

    public DbSettingRow Row { get; }
    public string Key => Row.Key;
    public string Default => Row.Default ?? "";
    public string Description => Row.Description;
    public bool IsChanged => Row.Changed;
    public bool IsReadOnly => Row.InitOnly;
    public string? Hint { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEdited))]
    public partial string Value { get; set; } = "";

    /// <summary>The value differs from what my.ini holds; Save sends only these rows.</summary>
    public bool IsEdited => !IsReadOnly && Value.Trim() != (Row.Current ?? "");

    /// <summary>What Save sends: null puts the server default back.</summary>
    public string? Change => Value.Trim() is { Length: > 0 } v ? v : null;

    /// <summary>Reset shows on rows my.ini changed; the mode's rows and creation-time rows are not the page's to reset.</summary>
    public bool CanBeReset => IsChanged && !IsReadOnly;

    [RelayCommand(CanExecute = nameof(CanBeReset))]
    private void Reset() => Value = "";

    partial void OnValueChanged(string value) => _edited();
}

/// <summary>A MariaDB or MySQL version the setup can use: installed already, or downloaded first.</summary>
public sealed record DbInstallChoice(string Label, ComponentRelease Release, bool Installed);

/// <summary>
/// The Database page: one engine active at a time, its status, the settings table next to the server's defaults, and setting up
/// (or switching to) an engine with a root password.
/// </summary>
public sealed partial class DatabaseViewModel : ObservableObject
{
    private readonly IAgentApi _api;
    private readonly AppPaths _paths;
    private readonly EditorLauncher _editor;
    private readonly Action<Action> _ui;
    private AppSettings _settings = new();
    private ServiceStatus? _status;

    public DatabaseViewModel(IAgentApi api, AppPaths paths, EditorLauncher editor, Action<Action>? ui = null)
    {
        _api = api;
        _paths = paths;
        _editor = editor;
        _ui = ui ?? (a => Dispatcher.UIThread.Post(a));
        Updates = new UpdatesPanel(api, k => k is ComponentKind.MariaDb or ComponentKind.MySql, LoadAsync);
        api.StatusChanged += (_, s) => _ui(() => ApplyStatus(s));
    }

    public UpdatesPanel Updates { get; }

    public ObservableCollection<DbSettingItem> Rows { get; } = [];
    public ObservableCollection<DbSettingItem> VisibleRows { get; } = [];
    public ObservableCollection<DbInstallChoice> InstallChoices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsActive), nameof(ShowsSetup), nameof(SetupTitle), nameof(SetupNote), nameof(SaveLabel), nameof(DataFolder),
        nameof(ActiveName))]
    public partial DbEngine? ActiveEngine { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsActive), nameof(ShowsSetup), nameof(SetupTitle), nameof(SetupNote), nameof(IsMariaDbSelected),
        nameof(IsMySqlSelected), nameof(SelectedName))]
    public partial DbEngine SelectedEngine { get; set; } = DbEngine.MariaDb;

    public bool IsMariaDbSelected => SelectedEngine == DbEngine.MariaDb;
    public bool IsMySqlSelected => SelectedEngine == DbEngine.MySql;
    public bool ShowsActive => ActiveEngine == SelectedEngine;
    public bool ShowsSetup => !ShowsActive;
    public string SelectedName => Name(SelectedEngine);
    public string ActiveName => ActiveEngine is { } e ? Name(e) : "Database";
    public string SaveLabel => $"Save and restart {ActiveName}";
    public string DataFolder => ActiveEngine is { } e ? new MyIni(_paths).DataDir(e) : "";
    public string SetupTitle => ActiveEngine is null ? $"Set up {SelectedName}" : $"Switch to {SelectedName}";

    public string SetupNote =>
        (ActiveEngine is { } a ? $"{Name(a)} stops: one database runs at a time. " : "") +
        $"If {SelectedName} was set up here before, enter the root password it was created with.";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartStopLabel))]
    public partial bool IsRunning { get; set; }

    public string StartStopLabel => IsRunning ? "Stop" : "Start";

    [ObservableProperty]
    public partial string ChangedText { get; set; } = "";

    [ObservableProperty]
    public partial bool OnlyChanged { get; set; }

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial DbInstallChoice? SelectedInstall { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial string RootPassword { get; set; } = "";

    [ObservableProperty]
    public partial bool IsChangingPassword { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyRootPasswordCommand))]
    public partial string NewRootPassword { get; set; } = "";

    [ObservableProperty]
    public partial string? ResultText { get; set; }

    [ObservableProperty]
    public partial bool ResultOk { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            _settings = await _api.GetSettingsAsync(ct);
            ActiveEngine = _settings.DbEngine;
            if (ActiveEngine is { } active) SelectedEngine = active;
            ApplyStatus(await _api.GetStatusAsync(ct));
            await RefreshRowsAsync(ct);
            await RefreshInstallChoicesAsync(ct);
            await Updates.RefreshAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Loading the Database page failed");
            Show(false, $"The Anvil Agent did not answer: {e.Message}");
        }
    }

    [RelayCommand]
    private async Task SelectEngineAsync(DbEngine engine)
    {
        if (SelectedEngine == engine) return;
        SelectedEngine = engine;
        RootPassword = "";
        await CallAsync(() => RefreshInstallChoicesAsync(default));
    }

    [RelayCommand]
    private Task StartStopAsync() => ApplyAsync(() => IsRunning ? _api.StopAsync(ServiceId.Database) : _api.StartAsync(ServiceId.Database), null);

    [RelayCommand]
    private void OpenMyIni()
    {
        if (ActiveEngine is { } e) _editor.Open(new MyIni(_paths).PathFor(e));
    }

    [RelayCommand]
    private async Task BackupAsync()
    {
        IsBusy = true;
        try
        {
            var file = await _api.BackupDatabaseAsync();
            Show(true, $"Every database was backed up to {file}");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Show(false, $"Nothing was backed up. {e.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSave() => Rows.Any(r => r.IsEdited);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var changes = Rows.Where(r => r.IsEdited).ToDictionary(r => r.Key, r => r.Change);
        var name = ActiveName;
        var restarted = IsRunning;
        var ok = await ApplyAsync(() => _api.SaveDbSettingsAsync(changes),
            restarted ? $"Saved. {name} restarted with the new settings." : $"Saved. {name} uses them the next time it starts.");
        if (ok) await CallAsync(() => RefreshRowsAsync(default));
    }

    private bool CanInstall() => SelectedInstall is not null && RootPassword.Length > 0;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        var choice = SelectedInstall!;
        var engine = SelectedEngine;
        var label = $"{Name(engine)} {choice.Release.Version}";
        IsBusy = true;
        try
        {
            if (!choice.Installed)
            {
                Show(true, $"Downloading {label}…");
                await _api.InstallAsync(choice.Release);
            }
            Show(true, $"Setting up {label}…");
            var result = await _api.InstallDatabaseAsync(engine, choice.Release.Version, RootPassword);
            if (!result.Ok)
            {
                Show(false, result.Error ?? $"{label} was not set up.");
                return;
            }
            RootPassword = "";
            await LoadAsync();
            Show(true, $"{label} is set up.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Show(false, $"{label} was not set up. {e.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ChangeRootPassword()
    {
        NewRootPassword = "";
        IsChangingPassword = !IsChangingPassword;
    }

    private bool CanApplyRootPassword() => NewRootPassword.Length > 0;

    [RelayCommand(CanExecute = nameof(CanApplyRootPassword))]
    private async Task ApplyRootPasswordAsync()
    {
        if (await ApplyAsync(() => _api.SetDbRootPasswordAsync(NewRootPassword), "Root password changed. Update it in the apps that log in as root."))
        {
            NewRootPassword = "";
            IsChangingPassword = false;
        }
    }

    partial void OnOnlyChangedChanged(bool value) => ApplyFilter();

    partial void OnFilterChanged(string value) => ApplyFilter();

    private async Task RefreshRowsAsync(CancellationToken ct)
    {
        var rows = ActiveEngine is null ? [] : await _api.GetDbSettingsAsync(ct);
        Rows.Clear();
        foreach (var row in rows) Rows.Add(new DbSettingItem(row, SaveCommand.NotifyCanExecuteChanged));
        var changed = Rows.Count(r => r.IsChanged);
        ChangedText = changed == 0 ? "Every setting is at its default" : $"{changed} changed from default";
        ApplyFilter();
        SaveCommand.NotifyCanExecuteChanged();
    }

    private void ApplyFilter()
    {
        var text = Filter.Trim();
        VisibleRows.Clear();
        foreach (var row in Rows.Where(r => (!OnlyChanged || r.IsChanged) &&
                     (text.Length == 0 || r.Key.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                      r.Description.Contains(text, StringComparison.OrdinalIgnoreCase))))
            VisibleRows.Add(row);
    }

    private async Task RefreshInstallChoicesAsync(CancellationToken ct)
    {
        var kind = SelectedEngine == DbEngine.MariaDb ? ComponentKind.MariaDb : ComponentKind.MySql;
        var available = await _api.GetAvailableAsync(kind, ct);
        var present = await _api.GetInstalledAsync(kind, ct);
        var name = Name(SelectedEngine);
        var installed = present.Select(i => i.Version).ToHashSet();
        var choices = available.Select(r => (Release: r, Installed: installed.Contains(r.Version)))
            .Concat(present.Where(i => available.All(r => r.Version != i.Version))
                .Select(i => (Release: new ComponentRelease(kind, i.Version, new Uri(i.Path), i.Sha256), Installed: true)))
            .OrderByDescending(c => c.Release.Version, Comparer<string>.Create(SemVer.Compare));
        InstallChoices.Clear();
        foreach (var (release, isInstalled) in choices)
        {
            var tags = new[] { release.Lts ? "LTS" : null, release.Notes, isInstalled ? "installed" : null }.OfType<string>();
            InstallChoices.Add(new DbInstallChoice(string.Join(" · ", tags.Prepend($"{name} {release.Version}")), release, isInstalled));
        }
        SelectedInstall = InstallChoices.FirstOrDefault(c => c.Installed)
            ?? InstallChoices.FirstOrDefault(c => c.Release.Lts)
            ?? InstallChoices.FirstOrDefault();
    }

    private void ApplyStatus(AgentStatus status)
    {
        _status = status.Services.FirstOrDefault(s => s.Id == ServiceId.Database);
        var state = _status?.State ?? ProcessState.Stopped;
        IsRunning = state is ProcessState.Starting or ProcessState.Running or ProcessState.Restarting;
        var where = state switch
        {
            ProcessState.Running => $"Running on 127.0.0.1:{_settings.DbPort}",
            ProcessState.Starting or ProcessState.Restarting => "Starting…",
            _ => state.ToString(),
        };
        StatusText = ActiveEngine is { } e ? $"{Name(e)} {_settings.DbVersion} · {where}" : "";
        if (state == ProcessState.Failed && _status?.Detail is { } detail) Show(false, detail);
    }

    private async Task<bool> ApplyAsync(Func<Task<ApplyResult>> call, string? success)
    {
        IsBusy = true;
        try
        {
            var result = await call();
            if (!result.Ok) Show(false, result.Error ?? "The Agent refused the change.");
            else if (success is not null) Show(true, success);
            return result.Ok;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Agent call failed");
            Show(false, $"The Anvil Agent did not answer: {e.Message}");
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CallAsync(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Agent call failed");
            Show(false, $"The Anvil Agent did not answer: {e.Message}");
        }
    }

    private void Show(bool ok, string text)
    {
        ResultOk = ok;
        ResultText = text;
    }

    private static string Name(DbEngine e) => e == DbEngine.MariaDb ? "MariaDB" : "MySQL";
}
