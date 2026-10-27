using System.Collections.ObjectModel;
using ArbiPache.App.Services;
using ArbiPache.Core.Agent;
using ArbiPache.Core.Modes;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace ArbiPache.App.ViewModels;

/// <summary>One value the switch changes; an empty value shows as a dash.</summary>
public sealed record ModeChangeRow(string Key, string From, string To);

/// <summary>The values one file (or the firewall) gets.</summary>
public sealed record ModeChangeGroup(string Name, string File, IReadOnlyList<ModeChangeRow> Rows)
{
    public bool HasFile => File.Length > 0;
}

/// <summary>
/// The "Switch to … mode?" dialog: the Agent's preview grouped by file, where the current files are backed up, Keep Xdebug
/// loaded for Production, and after the switch the firewall change the new mode needs.
/// </summary>
public sealed partial class ModeSwitchViewModel : ObservableObject
{
    private readonly IAgentApi _api;
    private readonly ElevationFollowUp _followUp;
    private readonly AppPaths _paths;
    private readonly ServiceSwitch? _service;

    /// <param name="service">Offered for Production when the Agent is not yet a Windows service; null offers nothing.</param>
    public ModeSwitchViewModel(IAgentApi api, ElevationFollowUp followUp, AppPaths paths, ServerMode from, ServerMode target, ServiceSwitch? service = null)
    {
        _api = api;
        _followUp = followUp;
        _paths = paths;
        _service = service;
        From = from;
        Target = target;
    }

    public ServerMode From { get; }
    public ServerMode Target { get; }
    public string Chip => $"{From} → {Target}";
    public string Title => $"Switch to {Target} mode?";
    public bool ToProduction => Target == ServerMode.Production;
    public bool ShowsKeepXdebug => ToProduction;
    public ObservableCollection<ModeChangeGroup> Groups { get; } = [];

    [ObservableProperty]
    public partial string Note { get; set; } = "Reading what changes…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackupPath))]
    public partial string BackupPath { get; set; } = "";

    public bool HasBackupPath => BackupPath.Length > 0;

    [ObservableProperty]
    public partial bool KeepXdebug { get; set; }

    /// <summary>Production without the service stops serving when the user signs out, so the dialog offers it.</summary>
    [ObservableProperty]
    public partial bool OffersService { get; set; }

    [ObservableProperty]
    public partial bool RunAsService { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool IsPreviewed { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>The switch happened; what is left to show is a problem with the follow-up.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CancelLabel), nameof(ShowsApply))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool IsDone { get; set; }

    public string CancelLabel => IsDone ? "Close" : "Cancel";
    public bool ShowsApply => !IsDone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; set; }

    public bool HasMessage => Message is not null;

    /// <summary>Raised when the dialog should close; true when the mode was switched.</summary>
    public event EventHandler<bool>? Closed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var preview = await _api.PreviewModeAsync(Target, ct);
            foreach (var group in preview.Changes.GroupBy(c => (c.Where, c.File)))
                Groups.Add(new ModeChangeGroup(group.Key.Where, group.Key.File, group.Select(c => new ModeChangeRow(c.Key, Dash(c.From), Dash(c.To))).ToList()));
            Note = RestartNote(preview);
            BackupPath = Path.GetRelativePath(_paths.Root, preview.BackupPreviewPath);
            OffersService = ToProduction && _service is not null && !(await _api.GetSettingsAsync(ct)).RunAsService;
            IsPreviewed = true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "The mode switch preview failed");
            Note = "";
            Message = e.Message;
        }
    }

    private bool CanApply() => IsPreviewed && !IsBusy && !IsDone;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        IsBusy = true;
        Message = null;
        try
        {
            var result = await _api.ApplyModeAsync(Target, new ModeOptions(KeepXdebug: ShowsKeepXdebug && KeepXdebug));
            if (!result.Ok)
            {
                Message = result.Error;
                return;
            }
            var settings = await _api.GetSettingsAsync();
            var problems = new List<string>();
            if (await _followUp.RunAsync(result, settings.HttpPort, settings.HttpsPort) is { } problem) problems.Add(problem);
            if (OffersService && RunAsService && _service is not null)
            {
                Message = "Moving ArbiPache to a Windows service…";
                if (await _service.TurnOnAsync() is { } moved) problems.Add(moved);
            }
            if (problems.Count > 0)
            {
                Message = $"Switched to {Target} mode. {string.Join(" ", problems)}";
                IsDone = true;
                return;
            }
            Closed?.Invoke(this, true);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "The mode switch failed");
            Message = $"The ArbiPache Agent did not answer: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => Closed?.Invoke(this, IsDone);

    private static string Dash(string value) => value.Length == 0 ? "—" : value;

    private static string RestartNote(ModeChangeSet preview)
    {
        if (preview.Restarts.Count == 0) return "These changes are written. Nothing that uses them is running, so nothing restarts.";
        var database = preview.Changes.FirstOrDefault(c => c.Where is "MariaDB" or "MySQL")?.Where ?? "the database";
        var names = preview.Restarts.Select(s => s switch
        {
            ServiceId.Php => "the PHP pools",
            ServiceId.Database => database,
            _ => "Apache",
        }).ToList();
        var list = names.Count == 1 ? names[0] : $"{string.Join(", ", names[..^1])} and {names[^1]}";
        return $"These changes are written, then ArbiPache restarts {list}.";
    }
}
