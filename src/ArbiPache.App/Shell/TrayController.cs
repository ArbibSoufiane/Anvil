using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace ArbiPache.App.Shell;

/// <summary>The notification-area icon: Open ArbiPache, Start all, Stop all, Quit.</summary>
public sealed class TrayController : IDisposable
{
    private readonly TrayIcon _icon;

    public TrayController(Application app, ShellViewModel vm, IClassicDesktopStyleApplicationLifetime desktop, WindowIcon icon)
    {
        var open = new NativeMenuItem("Open ArbiPache");
        open.Click += (_, _) => vm.ShowAndActivate();
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => desktop.Shutdown();

        // Start all / Stop all get their commands once the agent connection exists.
        StartAll = new NativeMenuItem("Start all") { IsEnabled = false };
        StopAll = new NativeMenuItem("Stop all") { IsEnabled = false };

        var menu = new NativeMenu();
        menu.Items.Add(open);
        menu.Items.Add(StartAll);
        menu.Items.Add(StopAll);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quit);

        _icon = new TrayIcon { Icon = icon, ToolTipText = "ArbiPache", Menu = menu };
        _icon.Clicked += (_, _) => vm.ShowAndActivate();
        TrayIcon.SetIcons(app, [_icon]);
    }

    public NativeMenuItem StartAll { get; }
    public NativeMenuItem StopAll { get; }

    public void Dispose() => _icon.Dispose();
}
