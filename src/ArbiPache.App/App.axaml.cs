using ArbiPache.App.Shell;
using ArbiPache.Core;
using ArbiPache.Core.Logging;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Serilog;

namespace ArbiPache.App;

public partial class App : Application
{
    private TrayController? _tray;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var paths = AppPaths.FromBaseDirectory();
            paths.EnsureCreated();
            Logging.Configure(paths, "app");
            Log.Information("ArbiPache {Version} started from {Root}", BuildInfo.Version, paths.Root);
            var store = new SettingsStore(paths);
            var settings = store.Load();
            if (store.LastLoadWarning is { } warning) Log.Warning("{Warning}", warning);

            var vm = new ShellViewModel(settings);
            var icon = AppIcon.Create();
            var window = new MainWindow(vm) { Icon = icon };

            // The window hides to the tray, so only Quit (or a real close) ends the app.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window.Closed += (_, _) => desktop.Shutdown();
            _tray = new TrayController(this, vm, desktop, icon);
            desktop.Exit += (_, _) =>
            {
                _tray.Dispose();
                Log.CloseAndFlush();
            };
            Program.Activated = _ => vm.ShowAndActivate();

            if (!(desktop.Args ?? []).Contains("--minimized")) window.Show();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
