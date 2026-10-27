using ArbiPache.App.Services;
using ArbiPache.App.Shell;
using ArbiPache.App.Tests.Support;
using ArbiPache.App.ViewModels;
using ArbiPache.App.Views;
using ArbiPache.Core.Agent;
using ArbiPache.Core.Apache;
using ArbiPache.Core.Components;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Preflight;
using ArbiPache.Core.Processes;
using ArbiPache.Core.Sites;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace ArbiPache.App.Tests;

public sealed class PageViewTests
{
    private static readonly AppPaths Paths = new(@"D:\ArbiPache");
    private readonly FakeAgentApi _api = new();

    private (MainWindow Window, ShellViewModel Shell) Show()
    {
        var editor = new EditorLauncher(() => "auto", new NoRegistry(), _ => false, "");
        var dashboard = new DashboardViewModel(_api, Paths, editor, ui: a => a());
        var apache = new ApacheViewModel(_api, Paths, editor, ui: a => a());
        var php = new PhpViewModel(_api, Paths, editor, ui: a => a());
        dashboard.LoadAsync().GetAwaiter().GetResult();
        apache.LoadAsync().GetAwaiter().GetResult();
        php.LoadAsync().GetAwaiter().GetResult();
        var shell = new ShellViewModel(_api.Settings, dashboard, apache, php: php);
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
    public void Php_view_lists_versions_with_tags_and_locks_xdebug()
    {
        _api.Php.Add(new PhpVersionInfo("8.5.12", "vs17", 4, 1000, 9100, ["htdocs"], ProcessState.Running, IsDefault: true, EndOfLife: false));
        _api.Php.Add(new PhpVersionInfo("7.4.33", "vc15", 2, 1000, 0, [], ProcessState.Stopped, IsDefault: false, EndOfLife: true));
        _api.Extensions["8.5.12"] = [new("intl", true, true, false, false)];
        _api.Installed.Add(new InstalledComponent(ComponentKind.Xdebug, "3.5.3", @"D:\ArbiPache\runtime\xdebug\3.5.3-php8.5", new string('b', 64),
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
        Assert.False(chips.Single(b => (string?)b.Content == "xdebug").IsEffectivelyEnabled);
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
        var sites = new SitesViewModel(_api, new FakeElevator(), new FakeCa(), new FakeFolderPicker(), Paths, () => ["192.168.10.2"], () => "192.168.10.2",
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
        _api.Htdocs.Add(new HtdocsEntry("blog", @"D:\ArbiPache\htdocs\blog", false, null, AccessLevel.Network));
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
        Assert.Equal(["Everyone (internet)", "My network only", "This PC only", "Specific IPs"], access);
        Assert.Equal("Create site", Named<Button>(window, "SaveSite").Content);
    }

    [AvaloniaFact]
    public async Task Client_certificate_choice_shows_its_file_fields()
    {
        var (window, shell, sites, _) = ShowSitePages();
        shell.NavigateCommand.Execute(AppPage.Sites);
        await sites.AddCommand.ExecuteAsync(null);
        window.UpdateLayout();
        Assert.False(Named<StackPanel>(window, "ImportFiles").IsEffectivelyVisible);

        sites.Editor!.Cert = CertSource.Imported;
        window.UpdateLayout();

        Assert.True(Named<StackPanel>(window, "ImportFiles").IsEffectivelyVisible);
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
        Assert.True(Named<TextBox>(window, "AcmeEmail").IsEffectivelyVisible);
    }
}
