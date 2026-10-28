using System.Collections.ObjectModel;
using Anvil.Core.Agent;
using Anvil.Core.Modes;
using Anvil.Core.Settings;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace Anvil.App.ViewModels;

/// <summary>One setting side by side in both profiles; the active mode's column is highlighted.</summary>
public sealed record ModeCompareRow(string Where, string Key, string Development, string Production, bool DevelopmentActive)
{
    public bool ProductionActive => !DevelopmentActive;
    public bool IsDifferent => Development != Production;
}

/// <summary>One profile value in the editor, next to the value Anvil ships with.</summary>
public sealed partial class ProfileValueItem(string where, string key, string builtIn, string value) : ObservableObject
{
    public string Where { get; } = where;
    public string Key { get; } = key;

    /// <summary>The built-in value; empty for a key the user added to the profile file.</summary>
    public string BuiltIn { get; } = builtIn;

    public string BuiltInText => BuiltIn.Length == 0 ? "—" : BuiltIn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEdited))]
    public partial string Value { get; set; } = value;

    public bool IsEdited => Value.Trim() != BuiltIn;
}

/// <summary>Edits one profile's values. Only values that differ from the built-in profile are saved.</summary>
public sealed partial class ProfileEditorViewModel : ObservableObject
{
    private readonly IAgentApi _api;
    private readonly ModeProfile _builtIn;
    private readonly Action<bool> _close;

    public ProfileEditorViewModel(IAgentApi api, ModeProfile profile, bool isActive, Action<bool> close)
    {
        _api = api;
        _close = close;
        Mode = profile.Mode;
        IsActive = isActive;
        _builtIn = BuiltInProfiles.For(profile.Mode);
        foreach (var (where, values, builtIn) in new[]
                 {
                     ("PHP", profile.Php, _builtIn.Php),
                     ("Apache", profile.Apache, _builtIn.Apache),
                     ("Database", profile.Database, _builtIn.Database),
                 })
        {
            foreach (var (key, value) in values) Items.Add(new ProfileValueItem(where, key, builtIn.GetValueOrDefault(key, ""), value));
        }
        Xdebug = profile.Xdebug;
        SecurityHeaders = profile.SecurityHeaders;
        DirectoryListing = profile.DirectoryListing;
    }

    public ServerMode Mode { get; }
    public bool IsActive { get; }
    public string Title => $"{Mode} profile";

    public string Note => IsActive
        ? $"{Mode} is the current mode: saving rewrites its files and restarts what changed, after a backup."
        : $"Saved for the next switch to {Mode}. Nothing restarts now.";

    public string SaveLabel => IsActive ? "Save and apply" : "Save";
    public ObservableCollection<ProfileValueItem> Items { get; } = [];

    [ObservableProperty]
    public partial bool Xdebug { get; set; }

    [ObservableProperty]
    public partial bool SecurityHeaders { get; set; }

    [ObservableProperty]
    public partial bool DirectoryListing { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; set; }

    public bool HasMessage => Message is not null;

    /// <summary>Every value back to the built-in one; keys the user added are dropped. Nothing is sent until Save.</summary>
    [RelayCommand]
    private void Reset()
    {
        foreach (var item in Items) item.Value = item.BuiltIn;
        Xdebug = _builtIn.Xdebug;
        SecurityHeaders = _builtIn.SecurityHeaders;
        DirectoryListing = _builtIn.DirectoryListing;
    }

    private bool CanSave() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        IsBusy = true;
        Message = null;
        try
        {
            var result = await _api.SaveModeProfileAsync(Mode, Overrides());
            if (result.Ok) _close(true);
            else Message = result.Error;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Saving the {Mode} profile failed", Mode);
            Message = $"The Anvil Agent did not answer: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _close(false);

    private ModeProfileOverrides Overrides()
    {
        IReadOnlyDictionary<string, string>? Part(string where)
        {
            var edited = Items.Where(i => i.Where == where && i.IsEdited).ToDictionary(i => i.Key, i => i.Value.Trim());
            return edited.Count == 0 ? null : edited;
        }

        bool? Flag(bool value, bool builtIn) => value == builtIn ? null : value;

        return new ModeProfileOverrides(Part("PHP"), Part("Apache"), Part("Database"),
            Flag(Xdebug, _builtIn.Xdebug), Flag(SecurityHeaders, _builtIn.SecurityHeaders), Flag(DirectoryListing, _builtIn.DirectoryListing));
    }
}

/// <summary>
/// The Dev / Production page: a card per mode (choosing the other one opens the switch dialog), both profiles side by side,
/// and the profile editor.
/// </summary>
public sealed partial class ModeViewModel : ObservableObject
{
    private readonly IAgentApi _api;
    private readonly Action<Action> _ui;
    private IReadOnlyList<ModeProfile> _profiles = [];

    public ModeViewModel(IAgentApi api, Action<Action>? ui = null)
    {
        _api = api;
        _ui = ui ?? (a => Dispatcher.UIThread.Post(a));
        api.StatusChanged += (_, s) => _ui(() =>
        {
            if (s.Mode == Mode) return;
            Mode = s.Mode;
            _ = LoadAsync();
        });
    }

    public ObservableCollection<ModeCompareRow> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDevelopment), nameof(IsProduction))]
    public partial ServerMode Mode { get; set; }

    public bool IsDevelopment => Mode == ServerMode.Development;
    public bool IsProduction => Mode == ServerMode.Production;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditor))]
    public partial ProfileEditorViewModel? Editor { get; set; }

    public bool HasEditor => Editor is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; set; }

    public bool HasMessage => Message is not null;

    /// <summary>The user chose the other mode: the shell opens the switch dialog.</summary>
    public event EventHandler<ServerMode>? SwitchRequested;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            Mode = (await _api.GetStatusAsync(ct)).Mode;
            _profiles = await _api.GetModeProfilesAsync(ct);
            BuildRows();
            Message = null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Loading the Dev / Production page failed");
            Message = $"The Anvil Agent did not answer: {e.Message}";
        }
    }

    [RelayCommand]
    private void SwitchTo(ServerMode target)
    {
        if (target != Mode) SwitchRequested?.Invoke(this, target);
    }

    [RelayCommand]
    private void EditProfile(ServerMode mode)
    {
        if (_profiles.FirstOrDefault(p => p.Mode == mode) is not { } profile) return;
        Editor = new ProfileEditorViewModel(_api, profile, mode == Mode, saved =>
        {
            Editor = null;
            if (saved) _ = LoadAsync();
        });
    }

    private void BuildRows()
    {
        Rows.Clear();
        if (_profiles.FirstOrDefault(p => p.Mode == ServerMode.Development) is not { } dev
            || _profiles.FirstOrDefault(p => p.Mode == ServerMode.Production) is not { } prod) return;

        var devActive = IsDevelopment;
        void Part(string where, IReadOnlyDictionary<string, string> d, IReadOnlyDictionary<string, string> p)
        {
            foreach (var key in d.Keys.Concat(p.Keys.Where(k => !d.ContainsKey(k))))
                Rows.Add(new ModeCompareRow(where, key, Dash(d.GetValueOrDefault(key, "")), Dash(p.GetValueOrDefault(key, "")), devActive));
        }

        Part("PHP", dev.Php, prod.Php);
        Rows.Add(new ModeCompareRow("PHP", "xdebug", Loaded(dev.Xdebug), Loaded(prod.Xdebug), devActive));
        Part("Apache", dev.Apache, prod.Apache);
        Rows.Add(new ModeCompareRow("Apache", "Options Indexes (htdocs)", OnOff(dev.DirectoryListing), OnOff(prod.DirectoryListing), devActive));
        Rows.Add(new ModeCompareRow("Apache", "Security headers", OnOff(dev.SecurityHeaders), OnOff(prod.SecurityHeaders), devActive));
        Part("Database", dev.Database, prod.Database);
    }

    private static string Dash(string value) => value.Length == 0 ? "—" : value;

    private static string Loaded(bool on) => on ? "Loaded" : "Not loaded";

    private static string OnOff(bool on) => on ? "On" : "Off";
}
