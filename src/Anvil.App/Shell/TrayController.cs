using Anvil.Core.Agent;
using Avalonia;
using Avalonia.Controls;
using Serilog;

namespace Anvil.App.Shell;

/// <summary>The notification-area icon: Open Anvil, Start all, Stop all, Quit.</summary>
public sealed class TrayController : IDisposable
{
    private readonly TrayIcon _icon;
    private IAgentApi? _api;

    /// <param name="quit">Runs when Quit is chosen; it asks about the services and ends the app.</param>
    public TrayController(Application app, ShellViewModel vm, Func<Task> quit, WindowIcon icon)
    {
        var open = new NativeMenuItem("Open Anvil");
        open.Click += (_, _) => vm.ShowAndActivate();
        var quitItem = new NativeMenuItem("Quit");
        quitItem.Click += async (_, _) => await quit();

        // Start all / Stop all are enabled once the Agent connection exists.
        StartAll = new NativeMenuItem("Start all") { IsEnabled = false };
        StartAll.Click += async (_, _) => await CallAsync(api => api.StartAsync(null));
        StopAll = new NativeMenuItem("Stop all") { IsEnabled = false };
        StopAll.Click += async (_, _) => await CallAsync(api => api.StopAsync(null));

        var menu = new NativeMenu();
        menu.Items.Add(open);
        menu.Items.Add(StartAll);
        menu.Items.Add(StopAll);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quitItem);

        _icon = new TrayIcon { Icon = icon, ToolTipText = "Anvil", Menu = menu };
        _icon.Clicked += (_, _) => vm.ShowAndActivate();
        TrayIcon.SetIcons(app, [_icon]);
    }

    public NativeMenuItem StartAll { get; }
    public NativeMenuItem StopAll { get; }

    public void Attach(IAgentApi api)
    {
        _api = api;
        StartAll.IsEnabled = true;
        StopAll.IsEnabled = true;
    }

    public void Dispose() => _icon.Dispose();

    private async Task CallAsync(Func<IAgentApi, Task<ApplyResult>> call)
    {
        if (_api is null) return;
        try
        {
            var result = await call(_api);
            if (!result.Ok) Log.Warning("Tray action failed: {Error}", result.Error);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Tray action failed");
        }
    }
}
