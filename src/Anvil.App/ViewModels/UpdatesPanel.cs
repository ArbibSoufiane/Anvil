using System.Collections.ObjectModel;
using Anvil.Core.Agent;
using Anvil.Core.Components;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace Anvil.App.ViewModels;

/// <summary>One newer patch release of a component in use.</summary>
public sealed class UpdateRow
{
    public UpdateRow(UpdateOffer offer, Func<UpdateRow, Task> update)
    {
        Offer = offer;
        UpdateCommand = new AsyncRelayCommand(() => update(this));
    }

    public UpdateOffer Offer { get; }

    public string Title => $"{UpdatesPanel.Name(Offer.Kind)} {Offer.Available} is available";

    public string Note => Offer.Release is null
        ? $"You use {Offer.Installed}. It is downloaded already: Anvil switches, and puts {Offer.Installed} back if the new version does not run."
        : $"You use {Offer.Installed}. Anvil downloads it, switches, and puts {Offer.Installed} back if the new version does not run.";

    public IAsyncRelayCommand UpdateCommand { get; }
}

/// <summary>
/// "Update available" on the Apache, PHP and Database pages. An update downloads the release when needed, then switches
/// to it; the Agent puts the previous version back when the new one does not run.
/// </summary>
/// <param name="shows">The component kinds this page shows.</param>
/// <param name="reload">Reloads the page once an update ran.</param>
public sealed partial class UpdatesPanel(IAgentApi api, Func<ComponentKind, bool> shows, Func<CancellationToken, Task> reload) : ObservableObject
{
    public ObservableCollection<UpdateRow> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShown))]
    public partial bool HasRows { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShown))]
    public partial string? Message { get; set; }

    /// <summary>Hidden when there is nothing to update and nothing to report, so the page keeps its spacing.</summary>
    public bool IsShown => HasRows || !string.IsNullOrEmpty(Message);

    [ObservableProperty]
    public partial bool MessageOk { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public static string Name(ComponentKind k) => k switch
    {
        ComponentKind.Php => "PHP",
        ComponentKind.MariaDb => "MariaDB",
        ComponentKind.MySql => "MySQL",
        _ => k.ToString(),
    };

    /// <summary>An Agent that cannot check leaves the list empty; the rest of the page still loads.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        IReadOnlyList<UpdateOffer> offers;
        try
        {
            offers = await api.CheckUpdatesAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Checking for updates failed");
            offers = [];
        }
        Rows.Clear();
        foreach (var offer in offers.Where(o => shows(o.Kind))) Rows.Add(new UpdateRow(offer, UpdateAsync));
        HasRows = Rows.Count > 0;
    }

    private async Task UpdateAsync(UpdateRow row)
    {
        if (IsBusy) return;
        IsBusy = true;
        var offer = row.Offer;
        var name = $"{Name(offer.Kind)} {offer.Available}";
        try
        {
            if (offer.Release is { } release)
            {
                Show(true, $"Downloading {name}…");
                try
                {
                    await api.InstallAsync(release);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Show(false, $"{name} was not installed: {e.Message}");
                    return;
                }
            }

            Show(true, $"Switching to {name}…");
            try
            {
                var result = await api.ActivateAsync(offer.Kind, offer.Available);
                Show(result.Ok, result.Ok ? $"{name} is now in use." : result.Error ?? $"{name} was not switched to.");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Show(false, $"The Anvil Agent did not answer: {e.Message}");
                return;
            }
            await reload(default);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Show(bool ok, string message)
    {
        MessageOk = ok;
        Message = message;
    }
}
