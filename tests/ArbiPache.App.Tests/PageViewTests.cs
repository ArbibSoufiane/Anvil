using ArbiPache.App.Services;
using ArbiPache.App.Shell;
using ArbiPache.App.Tests.Support;
using ArbiPache.App.ViewModels;
using ArbiPache.App.Views;
using ArbiPache.Core.Agent;
using ArbiPache.Core.Apache;
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
        dashboard.LoadAsync().GetAwaiter().GetResult();
        apache.LoadAsync().GetAwaiter().GetResult();
        var shell = new ShellViewModel(_api.Settings, dashboard, apache);
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
}
