using Anvil.App.Services;
using Anvil.Core;
using Anvil.Core.Update;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace Anvil.App.ViewModels;

/// <summary>
/// The update button at the bottom of the sidebar. It reads Check for updates; once a newer Anvil is found it reads
/// Download update, and clicking it again downloads that release and hands over to it. What happens shows in the toast.
/// </summary>
/// <param name="startServicesOnLaunch">Whether sites start again by themselves after the restart an update needs.</param>
public sealed partial class AppUpdateViewModel(IAppUpdate update, ToastViewModel toast, Func<bool> startServicesOnLaunch) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText), nameof(UpdateAvailable), nameof(Tip))]
    public partial Release? AvailableUpdate { get; set; }

    public bool UpdateAvailable => AvailableUpdate is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    [NotifyCanExecuteChangedFor(nameof(ClickCommand))]
    public partial bool Busy { get; set; }

    /// <summary>What the button says while it works, such as Downloading… 12.3 MB.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    public partial string? Progress { get; set; }

    public string ButtonText => Busy && Progress is { } progress ? progress : UpdateAvailable ? "Download update" : "Check for updates";

    /// <summary>The offered release and its notes, on hover.</summary>
    public string Tip => AvailableUpdate is { } next
        ? $"Anvil {next.Version}" + (string.IsNullOrWhiteSpace(next.Notes) ? "" : $"\n\n{next.Notes.Trim()}")
        : $"Anvil {BuildInfo.Version}. Asks GitHub for a newer version.";

    private bool CanClick => !Busy;

    [RelayCommand(CanExecute = nameof(CanClick))]
    private Task ClickAsync() => AvailableUpdate is null ? CheckAsync() : InstallAsync();

    /// <summary>Asks GitHub for a newer Anvil; only when the user clicks.</summary>
    private async Task CheckAsync()
    {
        if (update.CannotUpdate is { } reason)
        {
            toast.Show(reason, ok: false);
            return;
        }

        Busy = true;
        Progress = "Checking…";
        try
        {
            AvailableUpdate = await update.CheckAsync(CancellationToken.None);
            if (AvailableUpdate is not { } next) toast.Show($"All good: Anvil {BuildInfo.Version} is the latest version.", ok: true);
            else
                toast.Show($"Anvil {next.Version} is available. Download update installs it and restarts Anvil: " + (startServicesOnLaunch()
                    ? "sites stop for a moment and start again."
                    : "sites stop and stay stopped until you start them, because \"Start services when Anvil opens\" is off."), ok: true, stay: true);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Checking for an Anvil update failed");
            AvailableUpdate = null;
            toast.Show($"Anvil could not check for updates: {e.Message}", ok: false);
        }
        finally
        {
            Busy = false;
            Progress = null;
        }
    }

    /// <summary>Downloads the offered release and hands over to it; the window closes and the new version opens.</summary>
    private async Task InstallAsync()
    {
        if (AvailableUpdate is not { } next) return;
        Busy = true;
        Progress = "Downloading…";
        var progress = new Progress<long>(bytes =>
        {
            if (Busy) Progress = $"Downloading… {bytes / 1048576.0:0.0} MB";
        });
        var problem = await update.InstallAsync(next, progress, CancellationToken.None);
        Busy = false;
        Progress = null;
        if (problem is null) toast.Show($"Installing Anvil {next.Version}. It opens again in a moment.", ok: true, stay: true);
        else toast.Show(problem, ok: false, stay: true);
    }
}
