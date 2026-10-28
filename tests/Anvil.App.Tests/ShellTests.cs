using Anvil.App.Shell;
using Anvil.Core.Settings;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace Anvil.App.Tests;

public sealed class ShellTests
{
    [AvaloniaFact]
    public void Navigate_sets_title_and_subtitle()
    {
        var vm = new ShellViewModel(new AppSettings());

        vm.NavigateCommand.Execute(AppPage.Database);

        Assert.Equal(AppPage.Database, vm.Current);
        Assert.Equal("Database", vm.Title);
        Assert.Equal("MariaDB or MySQL, with every setting next to its default", vm.Subtitle);
        Assert.True(vm.NavItems.Single(n => n.Page == AppPage.Database).IsSelected);
        Assert.False(vm.NavItems.Single(n => n.Page == AppPage.Dashboard).IsSelected);
    }

    [AvaloniaFact]
    public void Nav_has_eight_items_in_order()
    {
        var vm = new ShellViewModel(new AppSettings());
        var window = new MainWindow(vm);
        window.Show();

        Assert.Equal(
            [AppPage.Dashboard, AppPage.Sites, AppPage.Apache, AppPage.Php, AppPage.Database, AppPage.Logs, AppPage.Settings],
            vm.NavItems.Select(n => n.Page));
        Assert.Equal(
            ["Dashboard", "Sites", "Apache", "PHP", "Database", "Logs", "Settings"],
            vm.NavItems.Select(n => n.Label));
        var nav = window.FindControl<ItemsControl>("Nav")!;
        Assert.Equal(7, nav.GetVisualDescendants().OfType<Button>().Count());
    }

    [AvaloniaFact]
    public void Clicking_nav_switches_page_view()
    {
        var vm = new ShellViewModel(new AppSettings());
        var window = new MainWindow(vm);
        window.Show();

        vm.NavigateCommand.Execute(AppPage.Logs);
        window.UpdateLayout();

        var content = window.FindControl<ContentControl>("PageHost")!;
        Assert.IsType<Anvil.App.Views.LogsView>(content.Presenter!.Child);
    }

    [AvaloniaFact]
    public void Closing_hides_window_when_close_to_tray()
    {
        var vm = new ShellViewModel(new AppSettings { CloseToTray = true });
        var window = new MainWindow(vm);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();

        window.Close();

        Assert.False(window.IsVisible);
        Assert.False(closed);
    }

    [AvaloniaFact]
    public void Closing_closes_window_when_close_to_tray_off()
    {
        var vm = new ShellViewModel(new AppSettings { CloseToTray = false });
        var window = new MainWindow(vm);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();

        window.Close();

        Assert.True(closed);
    }

    [AvaloniaFact]
    public void ShowAndActivate_restores_hidden_window()
    {
        var vm = new ShellViewModel(new AppSettings());
        var window = new MainWindow(vm);
        window.Show();
        window.Hide();

        vm.ShowAndActivate();

        Assert.True(window.IsVisible);
    }
}
