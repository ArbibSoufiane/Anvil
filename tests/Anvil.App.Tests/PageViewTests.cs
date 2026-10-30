using System.Net;
using Anvil.App.Services;
using Anvil.App.Shell;
using Anvil.App.Tests.Support;
using Anvil.App.ViewModels;
using Anvil.App.Views;
using Anvil.Core.Agent;
using Anvil.Core.Apache;
using Anvil.Core.Components;
using Anvil.Core.Database;
using Anvil.Core.Network;
using Anvil.Core.Paths;
using Anvil.Core.Php;
using Anvil.Core.Preflight;
using Anvil.Core.Processes;
using Anvil.Core.Redis;
using Anvil.Core.Settings;
using Anvil.Core.Sites;
using Anvil.Core.Update;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace Anvil.App.Tests;

public sealed class PageViewTests
{
    private static readonly AppPaths Paths = new(@"D:\Anvil");
    private readonly FakeAgentApi _api = new();

    private static readonly IReadOnlyList<LanAddress> Lan = [new("{ETHERNET}", "Ethernet", IPAddress.Parse("192.168.10.2"))];

    private (MainWindow Window, ShellViewModel Shell) Show()
    {
        var editor = new EditorLauncher(() => "auto", new NoRegistry(), _ => false, "");
        var dashboard = new DashboardViewModel(_api, Paths, editor, ui: a => a());
        var apache = new ApacheViewModel(_api, Paths, editor, ui: a => a());
        var php = new PhpViewModel(_api, Paths, editor, ui: a => a());
        var linked = new LinkedDatabasesViewModel(_api, new FakeElevator(), new FakeFolderPicker());
        var database = new DatabaseViewModel(linked);
        dashboard.LoadAsync().GetAwaiter().GetResult();
        apache.LoadAsync().GetAwaiter().GetResult();
        php.LoadAsync().GetAwaiter().GetResult();
        database.LoadAsync().GetAwaiter().GetResult();
        var shell = new ShellViewModel(_api.Settings, dashboard, apache, php: php, database: database);
        var window = new MainWindow(shell);
        window.Show();
        window.UpdateLayout();
        return (window, shell);
    }

    private static T Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [AvaloniaFact]
    public void Dashboard_view_shows_apache_state_and_findings()
    {
        _api.Status = _api.Status with
        {
            Services = [new ServiceStatus(ServiceId.Apache, ProcessState.Running, "2.4.69", null)],
            Findings = [new PreflightFinding("vcredist", Severity.Blocking, "The Microsoft Visual C++ x64 runtime is not installed.", "Install it.")],
        };

        var (window, _) = Show();

        Assert.IsType<DashboardView>(window.FindControl<ContentControl>("PageHost")!.Presenter!.Child);
        Assert.Equal("Running", Named<TextBlock>(window, "ApacheState").Text);
        var findings = Named<ItemsControl>(window, "Findings");
        Assert.True(findings.IsVisible);
        Assert.Contains(findings.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Install it.");
    }

    [AvaloniaFact]
    public void Dashboard_view_shows_php_card()
    {
        _api.Status = _api.Status with
        {
            Services =
            [
                new ServiceStatus(ServiceId.Apache, ProcessState.Running, "2.4.69", null),
                new ServiceStatus(ServiceId.Php, ProcessState.Running, "8.5.12", "8.5 ×4"),
            ],
        };

        var (window, _) = Show();

        Assert.Equal("Running", Named<TextBlock>(window, "PhpState").Text);
        Assert.Equal("PHP 8.5.12 NTS x64", Named<TextBlock>(window, "PhpDefault").Text);
        Assert.Equal("8.5 ×4", Named<TextBlock>(window, "PhpPools").Text);
    }

    [AvaloniaFact]
    public void Dashboard_view_has_no_database_card_and_closes_warnings()
    {
        _api.Status = _api.Status with { Findings = [new PreflightFinding("firewall-missing", Severity.Warning, "The firewall keeps other devices out.", null)] };

        var (window, _) = Show();

        Assert.Null(window.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "DbCard"));
        var close = Named<ItemsControl>(window, "Findings").GetVisualDescendants().OfType<Button>()
            .Single(b => ToolTip.GetTip(b) as string == "Close this warning");
        Assert.True(close.IsEffectivelyVisible);

        close.Command!.Execute(close.CommandParameter);
        window.UpdateLayout();

        Assert.False(Named<ItemsControl>(window, "Findings").IsVisible);
    }

    [AvaloniaFact]
    public void Redis_view_shows_its_details_with_copy_buttons_and_redis_insight()
    {
        var wsl = new RedisPageWsl();
        var redis = new RedisViewModel(new WslRedis(wsl), new FakeTextClipboard(), ui: a => a(),
            ping: (_, _, _, _) => Task.FromResult(new RedisPing(true, "Redis answers on 127.0.0.1:6379.")), delay: (_, _) => Task.CompletedTask);
        var shell = new ShellViewModel(_api.Settings, redis: redis);
        var window = new MainWindow(shell);
        window.Show();

        shell.NavigateCommand.Execute(AppPage.Redis);
        redis.LoadAsync().GetAwaiter().GetResult();
        window.UpdateLayout();

        Assert.IsType<RedisView>(window.FindControl<ContentControl>("PageHost")!.Presenter!.Child);
        Assert.True(Named<Border>(window, "RedisServer").IsVisible);
        Assert.False(Named<Border>(window, "RedisDebug").IsVisible);
        var rows = Named<ItemsControl>(window, "CredentialRows");
        Assert.Contains(rows.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "redis://default:Pw123@127.0.0.1:6379");
        Assert.Equal(5, rows.GetVisualDescendants().OfType<Button>().Count(b => b.Content as string == "Copy"));
        Assert.True(Named<Border>(window, "RedisInsight").IsVisible);
    }

    private sealed class RedisPageWsl : IWsl
    {
        public bool Exists => true;

        public Task<WslResult> RunAsync(IReadOnlyList<string> args, string? stdin, Action<string>? onLine, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(args[0] == "--list" ? new WslResult(0, "Ubuntu\n") : new WslResult(0, "id=ubuntu\ninstalled=yes\nrequirepass Pw123\n"));

        public void StartDetached(IReadOnlyList<string> args, string stdin) { }
    }

    [AvaloniaFact]
    public void Database_view_lists_your_servers_with_their_buttons()
    {
        _api.LinkedDbs.Add(new LinkedDbInfo("mariadb", DbEngine.MariaDb, "13.0.2", @"C:\Program Files\MariaDB 13.0\bin", "MariaDB", 3307, "root",
            ProcessState.Running, CanControl: true));
        var (window, shell) = Show();

        shell.NavigateCommand.Execute(AppPage.Database);
        window.UpdateLayout();

        var servers = Named<ItemsControl>(window, "LinkedServers");
        var texts = servers.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("MariaDB 13.0.2", texts);
        Assert.Contains("Windows service MariaDB · port 3307", texts);
        var buttons = servers.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string).ToList();
        Assert.Equal(["Refresh", "Stop", "Apply port", "Back up", "Unlink"], buttons);
        Assert.Equal("3307", servers.GetVisualDescendants().OfType<TextBox>().Single().Text);
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "Link a server…" && b.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Database_view_without_servers_shows_the_guide()
    {
        var (window, shell) = Show();

        shell.NavigateCommand.Execute(AppPage.Database);
        window.UpdateLayout();

        Assert.True(Named<TextBlock>(window, "NoServers").IsEffectivelyVisible);
        Assert.True(Named<Border>(window, "DbGuide").IsEffectivelyVisible);
        var downloads = Named<ItemsControl>(window, "DbDownloads").GetVisualDescendants().OfType<Button>().ToList();
        Assert.Equal(["Open download page", "Open download page"], downloads.Select(b => b.Content as string));
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "Refresh all" && b.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Php_view_lists_versions_with_tags_and_extension_chips()
    {
        _api.Php.Add(new PhpVersionInfo("8.5.12", "vs17", 4, 1000, 9100, ["htdocs"], ProcessState.Running, IsDefault: true, EndOfLife: false));
        _api.Php.Add(new PhpVersionInfo("7.4.33", "vc15", 2, 1000, 0, [], ProcessState.Stopped, IsDefault: false, EndOfLife: true));
        _api.Extensions["8.5.12"] = [new("intl", true, true, false), new("xdebug", false, true, true)];
        _api.PhpIni["8.5.12"] = new PhpIniValues(new Dictionary<string, string> { ["memory_limit"] = "1G" }, Opcache: true, OpcacheAvailable: true);
        var (window, shell) = Show();

        shell.NavigateCommand.Execute(AppPage.Php);
        window.UpdateLayout();

        Assert.IsType<PhpView>(window.FindControl<ContentControl>("PageHost")!.Presenter!.Child);
        var texts = Named<ItemsControl>(window, "PhpVersions").GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("PHP 8.5.12", texts);
        Assert.Contains("Default", texts);
        Assert.Contains("End of life", texts);
        var chips = Named<ItemsControl>(window, "Extensions").GetVisualDescendants().OfType<Button>().ToList();
        Assert.Equal(["intl"], chips.Select(b => b.Content as string));
        Assert.True(Named<Button>(window, "XdebugSwitch").IsEffectivelyVisible);
        Assert.DoesNotContain("on", Named<Button>(window, "XdebugSwitch").Classes);
        Assert.Contains("on", Named<Button>(window, "OpcacheSwitch").Classes);
        Assert.Equal("4", Named<TextBox>(window, "Workers").Text);
        Assert.Equal("127.0.0.1:9100", Named<TextBox>(window, "ListensOn").Text);
        var ini = Named<ItemsControl>(window, "IniFields").GetVisualDescendants().OfType<TextBox>().ToList();
        Assert.Equal(PhpIniSettings.Editable.Count, ini.Count);
        Assert.Equal("1G", ini[0].Text);
    }

    [AvaloniaFact]
    public void Apache_view_locks_required_module_chips()
    {
        var (window, shell) = Show();

        shell.NavigateCommand.Execute(AppPage.Apache);
        window.UpdateLayout();

        Assert.IsType<ApacheView>(window.FindControl<ContentControl>("PageHost")!.Presenter!.Child);
        var chips = Named<ItemsControl>(window, "Modules").GetVisualDescendants().OfType<Button>().ToList();
        Assert.Equal(ApacheModules.Required.Count + ApacheModules.Optional.Count, chips.Count);
        Assert.False(chips.Single(b => (string?)b.Content == "ssl").IsEffectivelyEnabled);
        Assert.True(chips.Single(b => (string?)b.Content == "status").IsEffectivelyEnabled);
        Assert.Equal("256", Named<TextBox>(window, "Threads").Text);
    }

    [AvaloniaFact]
    public void Pages_show_their_update_and_hide_the_banner_without_one()
    {
        _api.Offers.Add(new UpdateOffer(ComponentKind.Php, "8.5.5", "8.5.12", null));
        var (window, shell) = Show();

        shell.NavigateCommand.Execute(AppPage.Php);
        window.UpdateLayout();
        var banner = window.GetVisualDescendants().OfType<UpdatesBanner>().Single();
        Assert.True(banner.IsVisible);
        Assert.Contains(banner.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "PHP 8.5.12 is available");
        Assert.Contains(banner.GetVisualDescendants().OfType<Button>(), b => (string?)b.Content == "Update");

        shell.NavigateCommand.Execute(AppPage.Apache);
        window.UpdateLayout();
        Assert.False(window.GetVisualDescendants().OfType<UpdatesBanner>().Single().IsVisible);
    }

    [AvaloniaFact]
    public void Logs_view_lists_sources_and_colours_errors()
    {
        var root = Path.Combine(Path.GetTempPath(), "ap-logsview-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        Directory.CreateDirectory(Path.Combine(paths.Logs, "apache"));
        File.WriteAllText(Path.Combine(paths.Logs, "apache", "error.log"), "[core:error] AH00126: Invalid URI in request\n[mpm_winnt:notice] Child started\n");
        var editor = new EditorLauncher(() => "auto", new NoRegistry(), _ => false, "");
        using var logs = new LogsViewModel(paths, editor, ui: a => a()) { Follow = false };
        try
        {
            var window = new MainWindow(new ShellViewModel(_api.Settings, logs: logs));
            window.Show();

            ((ShellViewModel)window.DataContext!).NavigateCommand.Execute(AppPage.Logs);
            window.UpdateLayout();

            Assert.IsType<LogsView>(window.FindControl<ContentControl>("PageHost")!.Presenter!.Child);
            var chips = Named<ItemsControl>(window, "Sources").GetVisualDescendants().OfType<Button>().Select(b => b.Content).ToList();
            Assert.Equal(["All", "Apache"], chips);
            var texts = Named<ListBox>(window, "LogList").GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("logText")).ToList();
            Assert.Equal(2, texts.Count);
            Assert.Contains("error", texts.Single(t => t.Text!.Contains("AH00126")).Classes);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private (MainWindow Window, ShellViewModel Shell, SitesViewModel Sites, SettingsViewModel Settings) ShowSitePages()
    {
        var sites = new SitesViewModel(_api, new FakeElevator(), new FakeCa(), new FakeFolderPicker(), Paths, () => Lan, new FakeTextClipboard(),
            ui: a => a());
        var settings = new SettingsViewModel(_api, new FakeElevator(), new FakeRunKey(), new FakeCa(), new FakeFolderPicker(), Paths, () => Lan,
            new FakeTextClipboard(), ui: a => a());
        sites.LoadAsync().GetAwaiter().GetResult();
        settings.LoadAsync().GetAwaiter().GetResult();
        var shell = new ShellViewModel(_api.Settings, sites: sites, settingsPage: settings);
        var window = new MainWindow(shell);
        window.Show();
        window.UpdateLayout();
        return (window, shell, sites, settings);
    }

    [AvaloniaFact]
    public void Sites_view_lists_sites_and_htdocs()
    {
        _api.Sites.Add(new SiteDefinition { Id = "shop.test", Kind = SiteKind.Domain, Type = SiteType.Php, Name = "shop.test", Root = @"D:\Sites\shop" });
        _api.Htdocs.Add(new HtdocsEntry("blog", @"D:\Anvil\htdocs\blog", false, null, AccessLevel.Network));
        var (window, shell, _, _) = ShowSitePages();

        shell.NavigateCommand.Execute(AppPage.Sites);
        window.UpdateLayout();

        Assert.IsType<SitesView>(window.FindControl<ContentControl>("PageHost")!.Presenter!.Child);
        Assert.Contains(Named<ItemsControl>(window, "SiteRows").GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "shop.test");
        Assert.Contains(Named<ItemsControl>(window, "HtdocsRows").GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "localhost/blog");
        Assert.False(Named<AddSiteDialog>(window, "Editor").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Add_site_opens_the_form_with_kind_cards_and_access_choices()
    {
        var (window, shell, sites, _) = ShowSitePages();
        shell.NavigateCommand.Execute(AppPage.Sites);
        window.UpdateLayout();

        await sites.AddCommand.ExecuteAsync(null);
        window.UpdateLayout();

        Assert.True(Named<AddSiteDialog>(window, "Editor").IsEffectivelyVisible);
        Assert.Contains("selected", Named<Button>(window, "KindDomain").Classes);
        var access = Named<ItemsControl>(window, "AccessOptions").GetVisualDescendants().OfType<Button>().Select(b => b.Content).ToList();
        Assert.Equal(["This PC only", "My network"], access);
        Assert.Equal("Create site", Named<Button>(window, "SaveSite").Content);
    }

    [AvaloniaFact]
    public void Settings_view_shows_folders_and_ca_status()
    {
        var (window, shell, _, _) = ShowSitePages();

        shell.NavigateCommand.Execute(AppPage.Settings);
        window.UpdateLayout();

        Assert.IsType<SettingsView>(window.FindControl<ContentControl>("PageHost")!.Presenter!.Child);
        Assert.Contains(Named<Border>(window, "CaStatus").GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Not trusted");
        Assert.Equal("80", Named<TextBox>(window, "HttpPort").Text);
        Assert.False(Named<Button>(window, "CheckForUpdates").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Settings_view_offers_a_checked_update()
    {
        var update = new FakeAppUpdate
        {
            Available = new Release("4.1.0", new Uri("https://github.com/ArbibSoufiane/Anvil/releases/download/v4.1.0/Anvil-4.1.0-win64.zip"),
                new Uri("https://github.com/ArbibSoufiane/Anvil/releases/download/v4.1.0/SHA256SUMS"), "Faster starts."),
        };
        var settings = new SettingsViewModel(_api, new FakeElevator(), new FakeRunKey(), new FakeCa(), new FakeFolderPicker(), Paths, () => Lan,
            new FakeTextClipboard(), update: update, ui: a => a());
        settings.LoadAsync().GetAwaiter().GetResult();
        var shell = new ShellViewModel(_api.Settings, settingsPage: settings);
        var window = new MainWindow(shell);
        window.Show();
        shell.NavigateCommand.Execute(AppPage.Settings);
        window.UpdateLayout();
        Assert.True(Named<Button>(window, "CheckForUpdates").IsEffectivelyVisible);
        Assert.False(Named<Button>(window, "InstallUpdate").IsEffectivelyVisible);

        settings.CheckForUpdatesCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        window.UpdateLayout();

        Assert.True(Named<Button>(window, "InstallUpdate").IsEffectivelyVisible);
        Assert.Equal("Install 4.1.0", Named<Button>(window, "InstallUpdate").Content);
        Assert.Equal("Faster starts.", Named<TextBlock>(window, "UpdateNotes").Text);
        Assert.StartsWith("Anvil 4.1.0 is available.", Named<TextBlock>(window, "UpdateMessage").Text);
    }

    [AvaloniaFact]
    public void Settings_view_shows_the_colleague_command_with_copy_buttons()
    {
        _api.Settings = _api.Settings with { NetworkDomain = "arbib.pc" };
        var (window, shell, _, settings) = ShowSitePages();

        shell.NavigateCommand.Execute(AppPage.Settings);
        window.UpdateLayout();

        Assert.Equal("arbib.pc", Named<TextBox>(window, "NetworkDomain").Text);
        Assert.Equal(settings.WindowsCommand, Named<TextBlock>(window, "WindowsCommand").Text);
        Assert.Equal(settings.MacCommand, Named<TextBlock>(window, "MacCommand").Text);
        Assert.True(Named<Button>(window, "CopyWindowsCommand").IsEffectivelyVisible);
        Assert.True(Named<Button>(window, "CopyMacCommand").IsEffectivelyVisible);
        Assert.Equal("Off while Apache is stopped.", Named<TextBlock>(window, "NameServiceStatus").Text);
        Assert.Equal("192.168.10.2", Named<TextBlock>(window, "ThisPcAddress").Text);
    }

    [AvaloniaFact]
    public void Sites_view_lists_network_addresses()
    {
        _api.Settings = _api.Settings with { NetworkDomain = "arbib.pc" };
        _api.Sites.Add(new SiteDefinition { Id = "shop.test", Kind = SiteKind.Domain, Type = SiteType.Php, Name = "shop.test", Root = @"D:\Sites\shop" });
        _api.Htdocs.Add(new HtdocsEntry("blog", @"D:\Anvil\htdocs\blog", false, null, AccessLevel.Network));
        var (window, shell, _, _) = ShowSitePages();

        shell.NavigateCommand.Execute(AppPage.Sites);
        window.UpdateLayout();

        Assert.Contains(Named<ItemsControl>(window, "SiteRows").GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "http://shop.arbib.pc");
        var htdocs = Named<ItemsControl>(window, "HtdocsRows").GetVisualDescendants().OfType<TextBlock>().ToList();
        Assert.Contains(htdocs, t => t.Text == "http://192.168.10.2/blog");
        Assert.Contains(htdocs, t => t.Text == "http://blog.arbib.pc");
    }

    [AvaloniaFact]
    public void Dashboard_finding_shows_its_action_button()
    {
        _api.Status = _api.Status with
        {
            Findings = [new PreflightFinding(NetworkFindings.FirewallId, Severity.Warning, "Other devices cannot reach this PC yet.", "Open the firewall.")],
        };

        var (window, _) = Show();

        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "Open firewall" && b.IsEffectivelyVisible);
    }
}
