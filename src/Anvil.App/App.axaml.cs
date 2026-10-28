using Anvil.App.Services;
using Anvil.App.Shell;
using Anvil.App.ViewModels;
using Anvil.Core;
using Anvil.Core.Logging;
using Anvil.Core.Elevation;
using Anvil.Core.Network;
using Anvil.Core.Paths;
using Anvil.Core.Platform;
using Anvil.Core.Settings;
using Anvil.Core.Update;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Serilog;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Anvil.App;

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
            Log.Information("Anvil {Version} started from {Root}", BuildInfo.Version, paths.Root);
            // The Agent is the only writer of data\; the GUI reads settings once for the window's first paint.
            var store = new SettingsStore(paths);
            var settings = store.Load();
            if (store.LastLoadWarning is { } warning) Log.Warning("{Warning}", warning);

            _agent = new AgentConnection(paths);
            var http = new HttpClient();
            CleanUpUpdate(new Updater(paths, http));
            var appUpdate = AppUpdate.FromGitHub(paths, _agent.Api, _agent, http, () => Dispatcher.UIThread.Post(() => desktop.Shutdown()));
            var editorChoice = EditorChoice.ForUser();
            var editor = new EditorLauncher(() => editorChoice.Editor, new WindowsRegistryReader(), File.Exists);
            var dashboard = new DashboardViewModel(_agent.Api, paths, editor);
            var apache = new ApacheViewModel(_agent.Api, paths, editor);
            var php = new PhpViewModel(_agent.Api, paths, editor);
            var database = new DatabaseViewModel(_agent.Api, paths, editor);
            var logs = new LogsViewModel(paths, editor);
            var elevator = new UacElevator(paths);
            var ca = new LocalCaTrust(paths);
            MainWindow? window = null;
            var picker = new AvaloniaFolderPicker(() => window);
            static string? Lan() => LanAddresses.Find().FirstOrDefault()?.Address.ToString();
            var sites = new SitesViewModel(_agent.Api, elevator, ca, picker, paths, Lan);
            ShellViewModel vm = null!;
            var settingsPage = new SettingsViewModel(_agent.Api, elevator, new RegistryRunKey(), ca, picker, paths, Lan, s =>
            {
                settings = s;
                vm.CloseToTray = s.CloseToTray;
            }, () => _ = sites.LoadAsync(), appUpdate, editorChoice);
            vm = new ShellViewModel(settings, dashboard, apache, logs, php, sites, settingsPage, database);

            var icon = AppIcon.Create();
            window = new MainWindow(vm) { Icon = icon };

            // The window hides to the tray, so only Quit (or a real close) ends the app.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window.Closed += (_, _) => desktop.Shutdown();
            _tray = new TrayController(this, vm, () => QuitAsync(desktop), icon);
            desktop.Exit += (_, _) =>
            {
                _tray.Dispose();
                logs.Dispose();
                _agent.DisposeAsync().AsTask().GetAwaiter().GetResult();
                http.Dispose();
                Log.CloseAndFlush();
            };
            Program.Activated = _ => vm.ShowAndActivate();

            if (!(desktop.Args ?? []).Contains("--minimized")) window.Show();
            var pages = new Pages(dashboard, apache, php, database, sites, settingsPage);
            _agent.Reconnected += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadPagesAsync(pages));
            _ = ConnectAsync(pages);
        }
        base.OnFrameworkInitializationCompleted();
    }

    // Leftovers of the last update: its download, staging folder and all but the newest previous version.
    private static void CleanUpUpdate(Updater updater)
    {
        try
        {
            updater.CleanUp();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning(e, "Cleaning up after the last update failed");
        }
    }

    private sealed record Pages(DashboardViewModel Dashboard, ApacheViewModel Apache, PhpViewModel Php, DatabaseViewModel Database, SitesViewModel Sites,
        SettingsViewModel Settings);

    private async Task ConnectAsync(Pages pages)
    {
        try
        {
            var api = await _agent!.ConnectAsync(CancellationToken.None);
            _agentReady = true;
            _tray?.Attach(api);
            await LoadPagesAsync(pages);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Error(e, "Could not reach the Agent");
            pages.Dashboard.ApacheMessage = $"The Anvil Agent could not be started: {e.Message}";
        }
    }

    private static async Task LoadPagesAsync(Pages pages)
    {
        try
        {
            await pages.Dashboard.LoadAsync();
            await pages.Apache.LoadAsync();
            await pages.Php.LoadAsync();
            await pages.Database.LoadAsync();
            await pages.Sites.LoadAsync();
            await pages.Settings.LoadAsync();
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
                "Stop the web server and database too?\n\nYes: stop them and quit.\nNo: quit Anvil and keep them running.",
                "Quit Anvil", MESSAGEBOX_STYLE.MB_YESNOCANCEL | MESSAGEBOX_STYLE.MB_ICONQUESTION);
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
