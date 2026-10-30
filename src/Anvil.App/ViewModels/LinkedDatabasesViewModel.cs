using System.Collections.ObjectModel;
using System.Globalization;
using Anvil.App.Services;
using Anvil.Core.Agent;
using Anvil.Core.Database;
using Anvil.Core.Elevation;
using Anvil.Core.Processes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace Anvil.App.ViewModels;

/// <summary>One linked server in "Your servers".</summary>
public sealed partial class LinkedDbRow : ObservableObject
{
    public LinkedDbRow(LinkedDbInfo info, LinkedDatabasesViewModel owner)
    {
        Info = info;
        PortText = info.Port.ToString(CultureInfo.InvariantCulture);
        StartCommand = new AsyncRelayCommand(() => owner.StartAsync(this));
        StopCommand = new AsyncRelayCommand(() => owner.StopAsync(this));
        ApplyPortCommand = new AsyncRelayCommand(() => owner.ApplyPortAsync(this));
        BackupCommand = new AsyncRelayCommand(() => owner.BackupAsync(this));
        UnlinkCommand = new AsyncRelayCommand(() => owner.UnlinkAsync(this));
        RefreshCommand = new AsyncRelayCommand(() => owner.CheckAsync(this));
    }

    public LinkedDbInfo Info { get; }
    public string Title => $"{(Info.Engine == DbEngine.MariaDb ? "MariaDB" : "MySQL")} {Info.Version}";
    public string Detail => Info.Service is { } service ? $"Windows service {service} · port {Info.Port}" : $"Run by Anvil · port {Info.Port}";
    public string StatusText => Info.State.ToString();
    public bool IsRunning => Info.State == ProcessState.Running;

    [ObservableProperty]
    public partial string PortText { get; set; }

    /// <summary>What the last Refresh found: the server answers, or why not.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCheck))]
    public partial string? CheckText { get; set; }

    [ObservableProperty]
    public partial bool CheckOk { get; set; }

    public bool HasCheck => CheckText is not null;

    public IAsyncRelayCommand StartCommand { get; }
    public IAsyncRelayCommand StopCommand { get; }
    public IAsyncRelayCommand ApplyPortCommand { get; }
    public IAsyncRelayCommand BackupCommand { get; }
    public IAsyncRelayCommand UnlinkCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
}

/// <summary>
/// "Your servers" on the Database page: MariaDB and MySQL servers the user installed themselves. When the Agent says an
/// action needs Windows' permission, this asks through the Elevator, then repeats a call that could not finish without it.
/// </summary>
public sealed partial class LinkedDatabasesViewModel(IAgentApi api, IElevator elevator, IFolderPicker picker) : ObservableObject
{
    public ObservableCollection<LinkedDbRow> Servers { get; } = [];

    [ObservableProperty]
    public partial bool HasServers { get; set; }

    [ObservableProperty]
    public partial bool IsLinking { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LinkCommand))]
    public partial string LinkFolder { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LinkCommand))]
    public partial string LinkUser { get; set; } = "root";

    [ObservableProperty]
    public partial string LinkPassword { get; set; } = "";

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial bool MessageOk { get; set; }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var servers = await api.GetLinkedDatabasesAsync(ct);
            Servers.Clear();
            foreach (var s in servers) Servers.Add(new LinkedDbRow(s, this));
            HasServers = Servers.Count > 0;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Loading the linked databases failed");
            Show(false, $"The Anvil Agent did not answer: {e.Message}");
        }
    }

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        if (await picker.PickFolderAsync("Choose the bin folder of your MariaDB or MySQL server") is not { } folder) return;
        LinkFolder = folder;
        IsLinking = true;
        Message = null;
    }

    private bool CanLink() => LinkFolder.Length > 0 && LinkUser.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanLink))]
    private async Task LinkAsync()
    {
        var granted = false;
        var result = await RunAsync(() => api.LinkDatabaseAsync(LinkFolder, LinkUser.Trim(), LinkPassword), onGranted: () => granted = true);
        if (result is null) return;
        if (!result.Ok)
        {
            Show(false, result.Error);
            return;
        }
        IsLinking = false;
        LinkPassword = "";
        await LoadAsync();
        Show(true, granted ? "Linked. Start and Stop work here without Windows asking again." : result.Error ?? "Linked.");
    }

    [RelayCommand]
    private void CancelLink()
    {
        IsLinking = false;
        LinkPassword = "";
    }

    /// <summary>Checks every linked server: each must still run and let Anvil log in.</summary>
    [RelayCommand]
    private async Task RefreshAllAsync()
    {
        Message = null;
        var checks = new Dictionary<string, ApplyResult>();
        try
        {
            foreach (var row in Servers.ToList()) checks[row.Info.Id] = await api.CheckLinkedDatabaseAsync(row.Info.Id);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Checking the linked databases failed");
            Show(false, $"The Anvil Agent did not answer: {e.Message}");
            return;
        }
        await LoadAsync();
        foreach (var row in Servers)
            if (checks.TryGetValue(row.Info.Id, out var check)) ShowCheck(row, check);
    }

    internal async Task CheckAsync(LinkedDbRow row)
    {
        ApplyResult check;
        try
        {
            check = await api.CheckLinkedDatabaseAsync(row.Info.Id);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Checking linked database {Id} failed", row.Info.Id);
            Show(false, $"The Anvil Agent did not answer: {e.Message}");
            return;
        }
        await LoadAsync();
        if (Servers.FirstOrDefault(r => r.Info.Id == row.Info.Id) is { } fresh) ShowCheck(fresh, check);
    }

    private static void ShowCheck(LinkedDbRow row, ApplyResult check)
    {
        row.CheckOk = check.Ok;
        row.CheckText = check.Ok ? $"Running · Anvil logged in at {DateTime.Now:HH:mm:ss}" : check.Error;
    }

    internal Task StartAsync(LinkedDbRow row) => ActAsync(row, () => api.StartLinkedDatabaseAsync(row.Info.Id), $"{row.Title} is running.");

    internal Task StopAsync(LinkedDbRow row) => ActAsync(row, () => api.StopLinkedDatabaseAsync(row.Info.Id), $"{row.Title} is stopped.");

    internal async Task ApplyPortAsync(LinkedDbRow row)
    {
        if (!int.TryParse(row.PortText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            Show(false, "Enter a port number between 1 and 65535.");
            return;
        }
        await ActAsync(row, () => api.SetLinkedDatabasePortAsync(row.Info.Id, port), $"{row.Title} listens on port {port} now.");
    }

    internal async Task BackupAsync(LinkedDbRow row)
    {
        Show(true, $"Backing up {row.Title}…");
        try
        {
            Show(true, $"Backed up to {await api.BackupLinkedDatabaseAsync(row.Info.Id)}");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Show(false, $"The backup failed: {e.Message}");
        }
    }

    internal async Task UnlinkAsync(LinkedDbRow row)
    {
        string? warning = null;
        var result = await RunAsync(() => api.UnlinkDatabaseAsync(row.Info.Id),
            onRefused: error => warning = $" Windows did not take back your right to start and stop {row.Info.Service} ({error}).");
        if (result is null) return;
        await LoadAsync();
        Show(result.Ok, result.Ok ? $"{row.Title} is no longer linked. The server itself is unchanged.{warning}" : result.Error);
    }

    private async Task ActAsync(LinkedDbRow row, Func<Task<ApplyResult>> call, string success)
    {
        var result = await RunAsync(call);
        if (result is null) return;
        await LoadAsync();
        Show(result.Ok, result.Ok ? success : result.Error);
    }

    /// <summary>
    /// Makes the call. When its result names an administrator action, runs it; a failed call is then made once more. Null
    /// when the Agent did not answer or Windows refused, with the message shown.
    /// </summary>
    private async Task<ApplyResult?> RunAsync(Func<Task<ApplyResult>> call, Action? onGranted = null, Action<string>? onRefused = null)
    {
        try
        {
            var result = await call();
            if (result.Elevation is not { } request) return result;
            var elevated = await ElevateAsync(request);
            if (elevated is not null)
            {
                if (result.Ok)
                {
                    onRefused?.Invoke(elevated);
                    return onRefused is null
                        ? result with { Error = $"Linked. Windows did not allow starting and stopping it without asking ({elevated}); Start will ask again." }
                        : result;
                }
                Show(false, $"Windows did not allow it: {elevated}");
                return null;
            }
            onGranted?.Invoke();
            return result.Ok ? result : await call();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Linked database call failed");
            Show(false, $"The Anvil Agent did not answer: {e.Message}");
            return null;
        }
    }

    /// <returns>Null when Windows ran the action, else why not.</returns>
    private async Task<string?> ElevateAsync(ElevatedRequest request)
    {
        try
        {
            var r = await elevator.RunAsync(request, default);
            return r.Ok ? null : r.Error ?? "no reason given";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return e.Message;
        }
    }

    private void Show(bool ok, string? text)
    {
        MessageOk = ok;
        Message = text;
    }
}
