using Anvil.App.ViewModels;
using Anvil.Core;
using Anvil.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Anvil.App.Shell;

public sealed partial class ShellViewModel : ObservableObject
{
    // Copy from the mockup's `titles` map.
    private static readonly Dictionary<AppPage, (string Title, string Subtitle)> Titles = new()
    {
        [AppPage.Dashboard] = ("Dashboard", "Everything Anvil runs, at a glance"),
        [AppPage.Sites] = ("Sites", "Domain sites with HTTPS, and plain folders in htdocs"),
        [AppPage.Apache] = ("Apache", "Version, performance, modules and config files"),
        [AppPage.Php] = ("PHP", "Installed versions, worker pools and extensions"),
        [AppPage.Database] = ("Database", "MariaDB or MySQL, with every setting next to its default"),
        [AppPage.Mode] = ("Development or Production", "One switch rewrites PHP, Apache and database limits"),
        [AppPage.Logs] = ("Logs", "Apache, PHP, database and per-site logs in one place"),
        [AppPage.Settings] = ("Settings", "Startup, network access, folders and certificates"),
    };

    private readonly DashboardViewModel? _dashboard;
    private readonly ApacheViewModel? _apache;
    private readonly LogsViewModel? _logs;
    private readonly PhpViewModel? _php;
    private readonly DatabaseViewModel? _database;
    private readonly SitesViewModel? _sites;
    private readonly SettingsViewModel? _settings;
    private readonly ModeViewModel? _mode;
    private readonly Func<ServerMode, ServerMode, ModeSwitchViewModel>? _modeSwitch;

    public ShellViewModel(AppSettings settings, DashboardViewModel? dashboard = null, ApacheViewModel? apache = null, LogsViewModel? logs = null,
        PhpViewModel? php = null, SitesViewModel? sites = null, SettingsViewModel? settingsPage = null, DatabaseViewModel? database = null,
        ModeViewModel? mode = null, Func<ServerMode, ServerMode, ModeSwitchViewModel>? modeSwitch = null)
    {
        _mode = mode;
        _modeSwitch = modeSwitch;
        if (mode is not null) mode.SwitchRequested += (_, target) => _ = RequestModeCommand.ExecuteAsync(target);
        _dashboard = dashboard;
        _apache = apache;
        _logs = logs;
        _php = php;
        _database = database;
        _sites = sites;
        _settings = settingsPage;
        Mode = settings.Mode;
        CloseToTray = settings.CloseToTray;
        NavItems =
        [
            new(AppPage.Dashboard, "Dashboard", "M3 3 H10 V10 H3 Z M14 3 H21 V10 H14 Z M3 14 H10 V21 H3 Z M14 14 H21 V21 H14 Z"),
            new(AppPage.Sites, "Sites", "M21 12 A9 9 0 1 1 3 12 A9 9 0 1 1 21 12 Z M3 12 H21 M12 3 C15 6 15 18 12 21 C9 18 9 6 12 3"),
            new(AppPage.Apache, "Apache", "M3 4 H21 V11 H3 Z M3 13 H21 V20 H3 Z M7 7.5 H7.01 M7 16.5 H7.01"),
            new(AppPage.Php, "PHP", "M8 7 L3 12 L8 17 M16 7 L21 12 L16 17 M14 4 L10 20"),
            new(AppPage.Database, "Database", "M4 5.5 A8 2.5 0 1 1 20 5.5 A8 2.5 0 1 1 4 5.5 Z M4 5.5 V18.5 C4 19.9 7.6 21 12 21 C16.4 21 20 19.9 20 18.5 V5.5 M4 12 C4 13.4 7.6 14.5 12 14.5 C16.4 14.5 20 13.4 20 12"),
            new(AppPage.Mode, "Dev / Production", "M7 7 H17 A5 5 0 0 1 17 17 H7 A5 5 0 0 1 7 7 Z M19 12 A3 3 0 1 1 13 12 A3 3 0 1 1 19 12 Z"),
            new(AppPage.Logs, "Logs", "M8 6 H21 M8 12 H21 M8 18 H21 M3 6 H4 M3 12 H4 M3 18 H4"),
            new(AppPage.Settings, "Settings", "M15.5 12 A3.5 3.5 0 1 1 8.5 12 A3.5 3.5 0 1 1 15.5 12 Z M12 2 V5 M12 19 V22 M2 12 H5 M19 12 H22 M4.9 4.9 L7 7 M17 17 L19.1 19.1 M4.9 19.1 L7 17 M17 7 L19.1 4.9"),
        ];
        Select(AppPage.Dashboard);
    }

    public IReadOnlyList<NavItem> NavItems { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Subtitle), nameof(CurrentPage))]
    public partial AppPage Current { get; set; }

    /// <summary>What the content area shows: the page's view model, or the page itself while it has none yet.</summary>
    public object CurrentPage => Current switch
    {
        AppPage.Dashboard when _dashboard is not null => _dashboard,
        AppPage.Apache when _apache is not null => _apache,
        AppPage.Php when _php is not null => _php,
        AppPage.Database when _database is not null => _database,
        AppPage.Sites when _sites is not null => _sites,
        AppPage.Settings when _settings is not null => _settings,
        AppPage.Logs when _logs is not null => _logs,
        AppPage.Mode when _mode is not null => _mode,
        _ => Current,
    };

    // Each visit finds new logs (a site or PHP version added since) and shows the latest lines.
    partial void OnCurrentChanged(AppPage value)
    {
        if (value == AppPage.Logs) _logs?.Refresh();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeLabel), nameof(IsProduction), nameof(IsDevelopment))]
    public partial ServerMode Mode { get; set; }

    /// <summary>The switch dialog while it is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModeSwitch))]
    public partial ModeSwitchViewModel? ModeSwitch { get; set; }

    public bool HasModeSwitch => ModeSwitch is not null;

    public bool CloseToTray { get; set; }

    public string Title => Titles[Current].Title;
    public string Subtitle => Titles[Current].Subtitle;
    public string Version { get; } = BuildInfo.Version;
    public string ModeLabel => IsProduction ? "Production mode" : "Development mode";
    public bool IsProduction => Mode == ServerMode.Production;
    public bool IsDevelopment => !IsProduction;

    /// <summary>Raised when another launch or the tray asks for the window.</summary>
    public event EventHandler? ActivationRequested;

    /// <summary>Called on the UI thread by the activation server and the tray.</summary>
    public void ShowAndActivate() => ActivationRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Navigate(AppPage page) => Select(page);

    /// <summary>The header's Development / Production control and the Mode page's cards: opens the switch dialog for the other mode.</summary>
    [RelayCommand]
    private async Task RequestModeAsync(ServerMode target)
    {
        if (target == Mode || ModeSwitch is not null || _modeSwitch is null) return;
        var dialog = _modeSwitch(Mode, target);
        dialog.Closed += (_, switched) =>
        {
            ModeSwitch = null;
            if (switched) Mode = target;
        };
        ModeSwitch = dialog;
        await dialog.LoadAsync();
    }

    private void Select(AppPage page)
    {
        Current = page;
        foreach (var item in NavItems) item.IsSelected = item.Page == page;
    }
}
