using Anvil.App.Services;
using Anvil.App.Shell;
using Anvil.App.Tests.Support;
using Anvil.App.ViewModels;
using Anvil.Core.Paths;
using Anvil.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;

namespace Anvil.App.Tests;

public sealed class ThemeTests
{
    [Fact]
    public void The_choice_is_kept_in_a_file_and_unknown_means_dark()
    {
        var file = Path.Combine(Path.GetTempPath(), $"anvil-theme-{Guid.NewGuid():N}.txt");
        try
        {
            var choice = new ThemeChoice(file);
            Assert.Equal(ThemeChoice.Dark, choice.Theme);

            choice.Theme = "Light";
            Assert.Equal("light", File.ReadAllText(file));
            Assert.Equal(ThemeChoice.Light, new ThemeChoice(file).Theme);

            File.WriteAllText(file, "purple");
            Assert.Equal(ThemeChoice.Dark, new ThemeChoice(file).Theme);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Each_choice_maps_to_a_theme_variant()
    {
        Assert.Equal(ThemeVariant.Dark, ThemeChoice.Variant("dark"));
        Assert.Equal(ThemeVariant.Light, ThemeChoice.Variant("light"));
        Assert.Equal(ThemeVariant.Default, ThemeChoice.Variant("system"));
    }

    [Fact]
    public async Task Settings_switches_the_theme_at_once_and_keeps_it()
    {
        var theme = new ThemeChoice(null);
        var applied = new List<string>();
        var vm = new SettingsViewModel(new FakeAgentApi(), new FakeElevator(), new FakeRunKey(), new FakeCa(), new FakeFolderPicker(),
            new AppPaths(@"D:\Anvil"), () => [], new FakeTextClipboard(), ui: a => a(), theme: theme, applyTheme: applied.Add);
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(vm.IsDarkTheme);

        vm.UseThemeCommand.Execute("light");

        Assert.True(vm.IsLightTheme);
        Assert.False(vm.IsDarkTheme);
        Assert.Equal(["light"], applied);
        Assert.Equal(ThemeChoice.Light, theme.Theme);
    }

    [AvaloniaFact]
    public void Light_recolours_the_open_window()
    {
        var window = new MainWindow(new ShellViewModel(new AppSettings()));
        window.Show();
        var sidebar = window.FindControl<Border>("Sidebar")!;
        try
        {
            ThemeChoice.Apply(ThemeChoice.Dark);
            window.UpdateLayout();
            Assert.Equal(Color.Parse("#11151B"), ((ISolidColorBrush)sidebar.Background!).Color);

            ThemeChoice.Apply(ThemeChoice.Light);
            window.UpdateLayout();
            Assert.Equal(Color.Parse("#EEF0F3"), ((ISolidColorBrush)sidebar.Background!).Color);
        }
        finally
        {
            ThemeChoice.Apply(ThemeChoice.Dark);
        }
    }
}
