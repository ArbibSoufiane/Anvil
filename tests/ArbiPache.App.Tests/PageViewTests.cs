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
}
