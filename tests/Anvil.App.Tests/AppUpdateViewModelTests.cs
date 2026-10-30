using Anvil.App.Tests.Support;
using Anvil.App.ViewModels;
using Anvil.Core;
using Anvil.Core.Update;

namespace Anvil.App.Tests;

public sealed class AppUpdateViewModelTests
{
    private static readonly Release Next = new("4.1.0", new Uri("https://github.com/ArbibSoufiane/Anvil/releases/download/v4.1.0/Anvil-4.1.0-win64.zip"),
        new Uri("https://github.com/ArbibSoufiane/Anvil/releases/download/v4.1.0/SHA256SUMS"), "Faster starts.");

    private readonly FakeAppUpdate _update = new();
    private readonly ToastViewModel _toast = new((_, ct) => Task.Delay(Timeout.Infinite, ct));
    private bool _startServices = true;

    private AppUpdateViewModel Vm() => new(_update, _toast, () => _startServices);

    [Fact]
    public async Task No_update_shows_a_green_toast_and_keeps_the_button()
    {
        var vm = Vm();
        Assert.Equal("Check for updates", vm.ButtonText);

        await vm.ClickCommand.ExecuteAsync(null);

        Assert.Equal($"All good: Anvil {BuildInfo.Version} is the latest version.", _toast.Text);
        Assert.True(_toast.Ok);
        Assert.Equal("Check for updates", vm.ButtonText);
        Assert.False(vm.UpdateAvailable);
    }

    [Fact]
    public async Task A_newer_release_turns_the_button_into_download_update()
    {
        _update.Available = Next;
        var vm = Vm();

        await vm.ClickCommand.ExecuteAsync(null);

        Assert.Equal("Download update", vm.ButtonText);
        Assert.Equal("Anvil 4.1.0\n\nFaster starts.", vm.Tip);
        Assert.Equal("Anvil 4.1.0 is available. Download update installs it and restarts Anvil: sites stop for a moment and start again.", _toast.Text);
    }

    [Fact]
    public async Task The_offer_warns_when_sites_will_stay_stopped()
    {
        _startServices = false;
        _update.Available = Next;
        var vm = Vm();

        await vm.ClickCommand.ExecuteAsync(null);

        Assert.EndsWith("sites stop and stay stopped until you start them, because \"Start services when Anvil opens\" is off.", _toast.Text);
    }

    [Fact]
    public async Task Clicking_download_update_installs_the_offered_release()
    {
        _update.Available = Next;
        var vm = Vm();
        await vm.ClickCommand.ExecuteAsync(null);

        await vm.ClickCommand.ExecuteAsync(null);

        Assert.Equal(["check", "install 4.1.0"], _update.Calls);
        Assert.Equal("Installing Anvil 4.1.0. It opens again in a moment.", _toast.Text);
        Assert.True(_toast.Ok);
    }

    [Fact]
    public async Task A_failed_install_shows_a_red_toast_and_keeps_the_offer()
    {
        _update.Available = Next;
        _update.InstallProblem = "Anvil 4.1.0 was not installed: the download cannot be checked.";
        var vm = Vm();
        await vm.ClickCommand.ExecuteAsync(null);

        await vm.ClickCommand.ExecuteAsync(null);

        Assert.Equal(_update.InstallProblem, _toast.Text);
        Assert.False(_toast.Ok);
        Assert.Equal("Download update", vm.ButtonText);
    }

    [Fact]
    public async Task A_failed_check_shows_a_red_toast()
    {
        _update.CheckFails = new HttpRequestException("No such host is known. (api.github.com:443)");
        var vm = Vm();

        await vm.ClickCommand.ExecuteAsync(null);

        Assert.Equal("Anvil could not check for updates: No such host is known. (api.github.com:443)", _toast.Text);
        Assert.False(_toast.Ok);
        Assert.Equal("Check for updates", vm.ButtonText);
    }

    [Fact]
    public async Task A_development_build_explains_instead_of_checking()
    {
        _update.CannotUpdate = "This copy of Anvil was built from source, so it does not update itself.";
        var vm = Vm();

        await vm.ClickCommand.ExecuteAsync(null);

        Assert.Empty(_update.Calls);
        Assert.Equal(_update.CannotUpdate, _toast.Text);
    }
}

public sealed class ToastViewModelTests
{
    [Fact]
    public async Task A_toast_hides_by_itself_unless_it_stays()
    {
        var hide = new TaskCompletionSource();
        var toast = new ToastViewModel((_, _) => hide.Task);

        toast.Show("All good.", ok: true);
        Assert.True(toast.IsVisible);
        hide.SetResult();
        await Task.Yield();

        Assert.False(toast.IsVisible);
    }

    [Fact]
    public void Close_hides_it_at_once()
    {
        var toast = new ToastViewModel((_, ct) => Task.Delay(Timeout.Infinite, ct));
        toast.Show("Anvil 4.1.0 is available.", ok: true, stay: true);

        toast.CloseCommand.Execute(null);

        Assert.Null(toast.Text);
    }
}
