using Anvil.App.Services;
using Anvil.App.Shell;
using Anvil.App.Tests.Support;
using Anvil.App.ViewModels;
using Anvil.App.Views;
using Anvil.Core.Agent;
using Anvil.Core.Apache;
using Anvil.Core.Components;
using Anvil.Core.Database;
using Anvil.Core.Paths;
using Anvil.Core.Preflight;
using Anvil.Core.Processes;
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

    private (MainWindow Window, ShellViewModel Shell) Show()
    {
        var editor = new EditorLauncher(() => "auto", new NoRegistry(), _ => false, "");
        var dashboard = new DashboardViewModel(_api, Paths, editor, ui: a => a());
        var apache = new ApacheViewModel(_api, Paths, editor, ui: a => a());
        var php = new PhpViewModel(_api, Paths, editor, ui: a => a());
        var database = new DatabaseViewModel(_api, Paths, editor, ui: a => a());
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

    private void WithMariaDb()
    {
        _api.Settings = _api.Settings with { DbEngine = DbEngine.MariaDb, DbVersion = "11.8.9" };
        _api.Status = _api.Status with { Services = [new ServiceStatus(ServiceId.Database, ProcessState.Running, "MariaDB 11.8.9", null)] };
        _api.DbRows.AddRange(
        [
            new("innodb_buffer_pool_size", "1G", "134217728", "Memory for table and index data", true, false, false),
            new("max_connections", null, "151", "Most clients connected at once", false, false, false),
            new("lower_case_table_names", "1", "0", "How table names are stored", true, false, true),
        ]);
    }

    [AvaloniaFact]
    public void Dashboard_view_shows_database_card()
    {
        WithMariaDb();

        var (window, _) = Show();

        Assert.Equal("MariaDB", Named<TextBlock>(window, "DbTitle").Text);
        Assert.Equal("Running", Named<TextBlock>(window, "DbState").Text);
        Assert.Equal("127.0.0.1:3306", Named<TextBlock>(window, "DbListening").Text);
    }

    [AvaloniaFact]
    public void Database_view_shows_settings_next_to_defaults()
    {
        WithMariaDb();
        var (window, shell) = Show();

        shell.NavigateCommand.Execute(AppPage.Database);
        window.UpdateLayout();

        Assert.IsType<DatabaseView>(window.FindControl<ContentControl>("PageHost")!.Presenter!.Child);
        Assert.Equal("MariaDB 11.8.9 · Running on 127.0.0.1:3306", Named<TextBlock>(window, "DbStatus").Text);
        Assert.False(Named<Border>(window, "DbSetup").IsVisible);
        var table = Named<ItemsControl>(window, "DbSettings");
        var boxes = table.GetVisualDescendants().OfType<TextBox>().ToList();
        Assert.Equal(["1G", "", "1"], boxes.Select(b => b.Text));
        Assert.Equal([false, false, true], boxes.Select(b => b.IsReadOnly));
        Assert.Contains(table.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "134217728");
        Assert.Contains(table.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Fixed when the database is created.");
        var resets = table.GetVisualDescendants().OfType<Button>().Where(b => (string?)b.Content == "Reset").ToList();
        Assert.Single(resets, b => b.IsEffectivelyVisible);
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "Save and restart MariaDB");
    }

    [AvaloniaFact]
    public void Database_view_without_database_offers_setup()
    {
        var (window, shell) = Show();

        shell.NavigateCommand.Execute(AppPage.Database);
        window.UpdateLayout();

        Assert.True(Named<Border>(window, "DbSetup").IsVisible);
        Assert.False(Named<ItemsControl>(window, "DbSettings").IsEffectivelyVisible);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Set up MariaDB");
    }

    [AvaloniaFact]
    public void Php_view_lists_versions_with_tags_and_extension_chips()
    {
        _api.Php.Add(new PhpVersionInfo("8.5.12", "vs17", 4, 1000, 9100, ["htdocs"], ProcessState.Running, IsDefault: true, EndOfLife: false));
        _api.Php.Add(new PhpVersionInfo("7.4.33", "vc15", 2, 1000, 0, [], ProcessState.Stopped, IsDefault: false, EndOfLife: true));
        _api.Extensions["8.5.12"] = [new("intl", true, true, false)];
        _api.Installed.Add(new InstalledComponent(ComponentKind.Xdebug, "3.5.3", @"D:\Anvil\runtime\xdebug\3.5.3-php8.5", new string('b', 64),
            DateTimeOffset.Now, AppliesTo: "8.5"));
        var (window, shell) = Show();

        shell.NavigateCommand.Execute(AppPage.Php);
        window.UpdateLayout();

        Assert.IsType<PhpView>(window.FindControl<ContentControl>("PageHost")!.Presenter!.Child);
        var texts = Named<ItemsControl>(window, "PhpVersions").GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("PHP 8.5.12", texts);
        Assert.Contains("Default", texts);
        Assert.Contains("End of life", texts);
        var chips = Named<ItemsControl>(window, "Extensions").GetVisualDescendants().OfType<Button>().ToList();
        Assert.True(chips.Single(b => (string?)b.Content == "xdebug").IsEffectivelyEnabled);
        Assert.True(chips.Single(b => (string?)b.Content == "intl").IsEffectivelyEnabled);
        Assert.Equal("4", Named<TextBox>(window, "Workers").Text);
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
        var sites = new SitesViewModel(_api, new FakeElevator(), new FakeCa(), new FakeFolderPicker(), Paths, () => "192.168.10.2",
            ui: a => a());
        var settings = new SettingsViewModel(_api, new FakeElevator(), new FakeRunKey(), new FakeCa(), new FakeFolderPicker(), Paths, () => "192.168.10.2");
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
        var settings = new SettingsViewModel(_api, new FakeElevator(), new FakeRunKey(), new FakeCa(), new FakeFolderPicker(), Paths, () => "192.168.10.2",
            update: update);
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
}
