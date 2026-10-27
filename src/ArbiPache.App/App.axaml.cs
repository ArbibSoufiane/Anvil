using ArbiPache.App.Services;
using ArbiPache.App.Shell;
using ArbiPache.App.ViewModels;
using ArbiPache.Core;
using ArbiPache.Core.Logging;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Platform;
using ArbiPache.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Serilog;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ArbiPache.App;

public partial class App : Application
{
    private TrayController? _tray;
    private AgentConnection? _agent;
    private bool _agentReady;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var paths = AppPaths.FromBaseDirectory();
            Logging.Configure(paths, "app");
            Log.Information("ArbiPache {Version} started from {Root}", BuildInfo.Version, paths.Root);
            // The Agent is the only writer of data\; the GUI reads settings once for the window's first paint.
            var store = new SettingsStore(paths);
            var settings = store.Load();
            if (store.LastLoadWarning is { } warning) Log.Warning("{Warning}", warning);

            _agent = new AgentConnection(paths);
            var editor = new EditorLauncher(() => settings.Editor, new WindowsRegistryReader(), File.Exists);
            var dashboard = new DashboardViewModel(_agent.Api, paths, editor);
            var apache = new ApacheViewModel(_agent.Api, paths, editor);
            var vm = new ShellViewModel(settings, dashboard, apache);
            dashboard.CompareProfilesRequested += (_, _) => vm.NavigateCommand.Execute(AppPage.Mode);
            _agent.Api.StatusChanged += (_, s) => Dispatcher.UIThread.Post(() => vm.Mode = s.Mode);

            var icon = AppIcon.Create();
            var window = new MainWindow(vm) { Icon = icon };

            // The window hides to the tray, so only Quit (or a real close) ends the app.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window.Closed += (_, _) => desktop.Shutdown();
            _tray = new TrayController(this, vm, () => QuitAsync(desktop), icon);
            desktop.Exit += (_, _) =>
            {
                _tray.Dispose();
                _agent.DisposeAsync().AsTask().GetAwaiter().GetResult();
                Log.CloseAndFlush();
            };
            Program.Activated = _ => vm.ShowAndActivate();

            if (!(desktop.Args ?? []).Contains("--minimized")) window.Show();
            _agent.Reconnected += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadPagesAsync(dashboard, apache));
            _ = ConnectAsync(dashboard, apache);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private async Task ConnectAsync(DashboardViewModel dashboard, ApacheViewModel apache)
    {
        try
        {
            var api = await _agent!.ConnectAsync(CancellationToken.None);
            _agentReady = true;
            _tray?.Attach(api);
            await LoadPagesAsync(dashboard, apache);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Error(e, "Could not reach the Agent");
            dashboard.ApacheMessage = $"The ArbiPache Agent could not be started: {e.Message}";
        }
    }

    private static async Task LoadPagesAsync(DashboardViewModel dashboard, ApacheViewModel apache)
    {
        try
        {
            await dashboard.LoadAsync();
            await apache.LoadAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Loading pages from the Agent failed");
        }
    }

    /// <summary>Quit asks whether the services stop too; No leaves the Agent serving without a window.</summary>
    private async Task QuitAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_agentReady)
        {
            var answer = PInvoke.MessageBox(HWND.Null,
                "Stop the web server and database too?\n\nYes: stop them and quit.\nNo: quit ArbiPache and keep them running.",
                "Quit ArbiPache", MESSAGEBOX_STYLE.MB_YESNOCANCEL | MESSAGEBOX_STYLE.MB_ICONQUESTION);
            if (answer == MESSAGEBOX_RESULT.IDCANCEL) return;
            if (answer == MESSAGEBOX_RESULT.IDYES)
            {
                try
                {
                    await _agent!.Api.ShutdownAsync(stopServices: true).WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Log.Warning(e, "The Agent did not confirm shutdown");
                }
            }
        }
        desktop.Shutdown();
    }
}
