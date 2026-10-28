using Anvil.App.Services;
using Anvil.App.Tests.Support;
using Anvil.App.ViewModels;
using Anvil.Core;
using Anvil.Core.Agent;
using Anvil.Core.Elevation;
using Anvil.Core.Paths;
using Anvil.Core.Settings;
using Anvil.Core.Sites;
using Anvil.Core.Update;

namespace Anvil.App.Tests;

public sealed class SettingsViewModelTests
{
    private static readonly AppPaths Paths = new(@"D:\Anvil");
    private readonly FakeAgentApi _api = new();
    private readonly FakeElevator _elevator = new();
    private readonly FakeRunKey _runKey = new();
    private readonly FakeCa _ca = new();
    private readonly FakeFolderPicker _picker = new();
    private readonly List<AppSettings> _changed = [];
    private readonly EditorChoice _editor = new(null);
    private int _sitesChanged;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeAppUpdate _update = new();

    private static readonly Release Next = new("4.1.0", new Uri("https://github.com/ArbibSoufiane/Anvil/releases/download/v4.1.0/Anvil-4.1.0-win64.zip"),
        new Uri("https://github.com/ArbibSoufiane/Anvil/releases/download/v4.1.0/SHA256SUMS"), "Faster starts.");

    private async Task<SettingsViewModel> LoadedAsync(IAppUpdate? update = null)
    {
        var vm = new SettingsViewModel(_api, _elevator, _runKey, _ca, _picker, Paths, () => "192.168.10.2", _changed.Add, () => _sitesChanged++,
            update, _editor);
        await vm.LoadAsync(Ct);
        return vm;
    }

    [Fact]
    public async Task Check_for_updates_says_when_this_is_the_latest()
    {
        var vm = await LoadedAsync(update: _update);

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.True(vm.CanUpdate);
        Assert.Equal($"Anvil {BuildInfo.Version} is the latest version.", vm.UpdateMessage);
        Assert.False(vm.UpdateAvailable);
    }

    [Fact]
    public async Task Check_for_updates_offers_a_newer_release()
    {
        _update.Available = Next;
        var vm = await LoadedAsync(update: _update);

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.True(vm.UpdateAvailable);
        Assert.Equal("Install 4.1.0", vm.InstallUpdateText);
        Assert.Equal("Anvil 4.1.0 is available. Installing it restarts Anvil: sites stop for a moment and start again.", vm.UpdateMessage);
        Assert.Equal("Faster starts.", vm.UpdateNotes);
    }

    [Fact]
    public async Task The_offer_warns_when_sites_will_stay_stopped()
    {
        _api.Settings = _api.Settings with { StartServicesOnLaunch = false };
        _update.Available = Next;
        var vm = await LoadedAsync(update: _update);

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal("Anvil 4.1.0 is available. Installing it restarts Anvil: sites stop and stay stopped until you start them, "
            + "because \"Start services when Anvil opens\" is off.", vm.UpdateMessage);
    }

    [Fact]
    public async Task A_failed_check_says_why()
    {
        _update.CheckFails = new HttpRequestException("No such host is known. (api.github.com:443)");
        var vm = await LoadedAsync(update: _update);

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal("Anvil could not check for updates: No such host is known. (api.github.com:443)", vm.UpdateMessage);
        Assert.False(vm.UpdateOk);
        Assert.False(vm.UpdateAvailable);
    }

    [Fact]
    public async Task Install_hands_over_to_the_new_version()
    {
        _update.Available = Next;
        var vm = await LoadedAsync(update: _update);
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        await vm.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Equal(["check", "install 4.1.0"], _update.Calls);
        Assert.Equal("Installing Anvil 4.1.0. It opens again in a moment.", vm.UpdateMessage);
        Assert.True(vm.UpdateOk);
    }

    [Fact]
    public async Task A_failed_install_says_why_and_keeps_the_offer()
    {
        _update.Available = Next;
        _update.InstallProblem = "Anvil 4.1.0 was not installed: The release's SHA256SUMS does not list Anvil-4.1.0-win64.zip, so the download cannot be checked.";
        var vm = await LoadedAsync(update: _update);
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        await vm.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Equal(_update.InstallProblem, vm.UpdateMessage);
        Assert.False(vm.UpdateOk);
        Assert.True(vm.UpdateAvailable);
    }

    [Fact]
    public async Task A_development_build_explains_instead_of_checking()
    {
        _update.CannotUpdate = "This copy of Anvil was built from source, so it does not update itself. Build the new version instead.";
        var vm = await LoadedAsync(update: _update);

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Empty(_update.Calls);
        Assert.Equal(_update.CannotUpdate, vm.UpdateMessage);
    }

    [Fact]
    public async Task Without_an_updater_there_is_nothing_to_check()
    {
        var vm = await LoadedAsync();

        Assert.False(vm.CanUpdate);
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.Null(vm.UpdateMessage);
    }

    [Fact]
    public async Task StartWithWindows_sets_run_key_with_minimized()
    {
        var vm = await LoadedAsync();

        vm.StartWithWindows = true;
        await vm.ApplyGeneralCommand.ExecuteAsync(null);

        Assert.Equal("\"D:\\Anvil\\Anvil.exe\" --minimized", _runKey.Command);
        Assert.True(_api.Saved?.StartWithWindows);
        Assert.True(_changed.Single().StartWithWindows);

        vm.StartWithWindows = false;
        await vm.ApplyGeneralCommand.ExecuteAsync(null);

        Assert.Null(_runKey.Command);
        Assert.False(_api.Saved?.StartWithWindows);
    }

    [Fact]
    public async Task Rejected_settings_put_the_switches_back()
    {
        _api.SaveResult = ApplyResult.Failure("Apache refused the change.");
        var vm = await LoadedAsync();

        vm.CloseToTray = false;
        await vm.ApplyGeneralCommand.ExecuteAsync(null);

        Assert.True(vm.CloseToTray);
        Assert.False(vm.MessageOk);
        Assert.Equal("Apache refused the change.", vm.Message);
        Assert.Empty(_changed);
    }

    [Fact]
    public async Task Turning_htdocs_onto_the_network_asks_for_the_agents_firewall_request()
    {
        _api.Settings = _api.Settings with { Network = new NetworkAccess(false) };
        _api.FirewallRequest = new FirewallSetRequest("2.4.69", [80, 443], false);
        var vm = await LoadedAsync();

        vm.HtdocsOnNetwork = true;
        await vm.ApplyNetworkCommand.ExecuteAsync(null);

        Assert.Equal(new NetworkAccess(true), _api.Saved?.Network);
        Assert.Equal(_api.FirewallRequest, Assert.IsType<FirewallSetRequest>(Assert.Single(_elevator.Requests)));
    }

    [Fact]
    public async Task Nothing_needed_asks_windows_nothing()
    {
        _api.FirewallRequest = null;
        var vm = await LoadedAsync();

        vm.HtdocsOnNetwork = false;
        await vm.ApplyNetworkCommand.ExecuteAsync(null);
        vm.HttpPortText = "8080";
        await vm.ApplyPortsCommand.ExecuteAsync(null);

        Assert.Empty(_elevator.Requests);
    }

    [Fact]
    public async Task A_port_change_asks_for_the_agents_firewall_request()
    {
        _api.Settings = _api.Settings with { Network = new NetworkAccess(false) };
        var vm = await LoadedAsync();
        _api.FirewallRequest = new FirewallSetRequest("2.4.69", [8080, 443], false);

        vm.HttpPortText = "8080";
        await vm.ApplyPortsCommand.ExecuteAsync(null);

        Assert.Equal(8080, _api.Saved?.HttpPort);
        Assert.Equal(_api.FirewallRequest, Assert.IsType<FirewallSetRequest>(Assert.Single(_elevator.Requests)));
    }

    [Fact]
    public async Task A_refused_firewall_says_other_devices_cannot_reach_this_pc()
    {
        _api.FirewallRequest = new FirewallSetRequest("2.4.69", [80, 443], false);
        _elevator.Next = new ElevatedResult(false, "Permission was not granted.");
        var vm = await LoadedAsync();

        await vm.ApplyNetworkCommand.ExecuteAsync(null);

        Assert.False(vm.MessageOk);
        Assert.Equal("Windows did not let Anvil open the firewall (Permission was not granted.). Other devices cannot reach this PC until it does.", vm.Message);
    }

    [Fact]
    public async Task Ports_are_checked_before_saving()
    {
        var vm = await LoadedAsync();

        vm.HttpPortText = "eighty";
        await vm.ApplyPortsCommand.ExecuteAsync(null);

        Assert.Null(_api.Saved);
        Assert.Equal("Ports are whole numbers from 1 to 65535.", vm.PortsError);

        vm.HttpPortText = "8080";
        await vm.ApplyPortsCommand.ExecuteAsync(null);

        Assert.Equal(8080, _api.Saved?.HttpPort);
        Assert.Null(vm.PortsError);
    }

    [Fact]
    public async Task Ca_trust_follows_the_buttons()
    {
        var vm = await LoadedAsync();
        Assert.Equal("Not trusted", vm.CaStatus);

        vm.TrustCaCommand.Execute(null);
        Assert.Equal("Trusted", vm.CaStatus);

        vm.UntrustCaCommand.Execute(null);
        Assert.Equal("Not trusted", vm.CaStatus);
    }

    [Fact]
    public async Task Reissue_calls_the_agent()
    {
        var vm = await LoadedAsync();

        await vm.ReissueCommand.ExecuteAsync(null);

        Assert.Contains("reissue", _api.Calls);
        Assert.True(vm.MessageOk);
    }

    [Fact]
    public async Task Import_reports_and_updates_hosts()
    {
        _api.Import = new ImportReport(["afaq.test", "old.test"], ["shop.test: already exists"], ["7.4.9"]);
        _api.NextResult = ApplyResult.Success() with { HostsDomains = ["afaq.test"] };
        var vm = await LoadedAsync();

        await vm.ImportCommand.ExecuteAsync(null);

        Assert.Equal(["afaq.test"], Assert.IsType<HostsSetRequest>(Assert.Single(_elevator.Requests)).Domains);
        Assert.Contains("Imported 2 sites: afaq.test, old.test.", vm.ImportMessage);
        Assert.Contains("Skipped shop.test: already exists.", vm.ImportMessage);
        Assert.Contains("PHP 7.4.9", vm.ImportMessage);
        Assert.Equal(1, _sitesChanged);
    }

    [Fact]
    public async Task Htdocs_folder_change_saves_the_path()
    {
        _picker.Folder = @"E:\www";
        var vm = await LoadedAsync();

        await vm.ChangeHtdocsCommand.ExecuteAsync(null);

        Assert.Equal(@"E:\www", _api.Saved?.HtdocsPath);
        Assert.Equal(@"E:\www", vm.HtdocsFolder);
    }

    [Fact]
    public async Task Chosen_editor_is_saved()
    {
        _picker.Program = @"C:\Tools\subl.exe";
        var vm = await LoadedAsync();

        await vm.ChooseEditorCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\Tools\subl.exe", _editor.Editor);
        Assert.Equal(@"C:\Tools\subl.exe", vm.EditorText);
        // data\ is writable by the service account: a program named there could be planted by a script a site runs.
        Assert.Null(_api.Saved);
    }

    [Fact]
    public async Task Automatic_editor_is_shown_until_one_is_chosen()
    {
        var vm = await LoadedAsync();

        Assert.Equal(SettingsViewModel.AutomaticEditor, vm.EditorText);
        _editor.Editor = @"C:\Tools\subl.exe";
        vm.UseAutomaticEditorCommand.Execute(null);

        Assert.Equal("auto", _editor.Editor);
        Assert.Equal(SettingsViewModel.AutomaticEditor, vm.EditorText);
    }
}
