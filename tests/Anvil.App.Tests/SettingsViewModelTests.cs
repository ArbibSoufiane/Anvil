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

    private async Task<SettingsViewModel> LoadedAsync(ServiceSwitch? service = null, IAppUpdate? update = null)
    {
        var vm = new SettingsViewModel(_api, _elevator, _runKey, _ca, _picker, Paths, () => "192.168.10.2", _changed.Add, () => _sitesChanged++, service,
            update, _editor);
        await vm.LoadAsync(Ct);
        return vm;
    }

    private ServiceSwitch Service(Func<IReadOnlyList<string>?>? folderOpenTo = null) => new(_api, new FakeAgentLink(_api.Calls), _elevator,
        new FakeAgentService(_api.Calls), Paths, "S-1-5-21-1004336348-1177238915-682003330-1001", folderOpenTo ?? (() => null));

    /// <summary>A folder Authenticated Users can change until the Elevator secures it.</summary>
    private ServiceSwitch OpenFolderService() =>
        Service(() => _elevator.Requests.Any(r => r is FolderSecureRequest) ? [] : [@"NT AUTHORITY\Authenticated Users"]);

    private async Task<SettingsViewModel> RefusedForTheFolderAsync()
    {
        var vm = await LoadedAsync(OpenFolderService());
        vm.RunAsService = true;
        await vm.ApplyServiceCommand.ExecuteAsync(null);
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
    public async Task Turning_on_the_service_moves_the_agent()
    {
        var vm = await LoadedAsync(Service());

        vm.RunAsService = true;
        await vm.ApplyServiceCommand.ExecuteAsync(null);

        Assert.Contains(_elevator.Requests, r => r is ServiceInstallRequest);
        Assert.True(vm.RunAsService);
        Assert.True(_changed.Single().RunAsService);
        Assert.Equal("Anvil now runs as a Windows service. Sites stay up after you sign out or restart Windows.", vm.Message);
        Assert.True(vm.MessageOk);
    }

    [Fact]
    public async Task Turning_off_the_service_brings_the_agent_back()
    {
        _api.Settings = _api.Settings with { RunAsService = true };
        var vm = await LoadedAsync(Service());

        vm.RunAsService = false;
        await vm.ApplyServiceCommand.ExecuteAsync(null);

        Assert.Contains(_elevator.Requests, r => r is ServiceUninstallRequest);
        Assert.False(vm.RunAsService);
        Assert.Equal("Anvil runs from this window again. Sites stop when you quit Anvil.", vm.Message);
    }

    [Fact]
    public async Task A_refused_service_move_puts_the_switch_back()
    {
        _elevator.Next = new ElevatedResult(false, "The operation was canceled by the user.");
        var vm = await LoadedAsync(Service());

        vm.RunAsService = true;
        await vm.ApplyServiceCommand.ExecuteAsync(null);

        Assert.False(vm.RunAsService);
        Assert.False(vm.MessageOk);
        Assert.Equal("Windows did not let Anvil give its service access to the site folders (The operation was canceled by the user.). "
            + "Anvil keeps running as before, without the service.", vm.Message);
    }

    [Fact]
    public async Task A_folder_others_can_change_offers_to_secure_it()
    {
        var vm = await RefusedForTheFolderAsync();

        Assert.False(vm.RunAsService);
        Assert.False(vm.MessageOk);
        Assert.True(vm.CanSecureFolder);
        Assert.False(vm.ConfirmingSecureFolder);
        Assert.Empty(_elevator.Requests);
    }

    [Fact]
    public async Task Securing_the_folder_asks_first_then_moves_the_agent()
    {
        var vm = await RefusedForTheFolderAsync();

        await vm.SecureFolderCommand.ExecuteAsync(null);

        Assert.True(vm.ConfirmingSecureFolder);
        Assert.Empty(_elevator.Requests);
        Assert.Equal(@"Only you, administrators and Windows will be able to change files in D:\Anvil. NT AUTHORITY\Authenticated Users "
            + "will still read them but no longer change them, so another account or program that writes there stops working. "
            + "Windows asks you to confirm.", vm.SecureFolderText);

        await vm.SecureFolderCommand.ExecuteAsync(null);

        Assert.IsType<FolderSecureRequest>(_elevator.Requests[0]);
        Assert.Contains(_elevator.Requests, r => r is ServiceInstallRequest);
        Assert.True(vm.RunAsService);
        Assert.False(vm.CanSecureFolder);
        Assert.False(vm.ConfirmingSecureFolder);
        Assert.Equal("Anvil now runs as a Windows service. Sites stay up after you sign out or restart Windows.", vm.Message);
    }

    [Fact]
    public async Task Keeping_the_folder_as_it_is_changes_nothing()
    {
        var vm = await RefusedForTheFolderAsync();
        await vm.SecureFolderCommand.ExecuteAsync(null);

        vm.CancelSecureFolderCommand.Execute(null);

        Assert.False(vm.ConfirmingSecureFolder);
        Assert.True(vm.CanSecureFolder);
        Assert.Empty(_elevator.Requests);
        Assert.False(vm.RunAsService);
    }

    [Fact]
    public async Task A_folder_fixed_meanwhile_moves_the_agent_without_asking()
    {
        var open = true;
        var vm = await LoadedAsync(Service(() => open ? [@"NT AUTHORITY\Authenticated Users"] : []));
        vm.RunAsService = true;
        await vm.ApplyServiceCommand.ExecuteAsync(null);
        open = false;

        await vm.SecureFolderCommand.ExecuteAsync(null);

        Assert.DoesNotContain(_elevator.Requests, r => r is FolderSecureRequest);
        Assert.Contains(_elevator.Requests, r => r is ServiceInstallRequest);
        Assert.True(vm.RunAsService);
        Assert.False(vm.CanSecureFolder);
    }

    [Fact]
    public async Task A_refused_folder_change_keeps_the_offer_and_the_window()
    {
        _elevator.ByVerb["folder-secure"] = new ElevatedResult(false, "The operation was canceled by the user.");
        var vm = await RefusedForTheFolderAsync();

        await vm.SecureFolderCommand.ExecuteAsync(null);
        await vm.SecureFolderCommand.ExecuteAsync(null);

        Assert.Equal("Windows did not let Anvil secure its folder (The operation was canceled by the user.).", vm.Message);
        Assert.False(vm.MessageOk);
        Assert.True(vm.CanSecureFolder);
        Assert.False(vm.RunAsService);
        Assert.DoesNotContain(_elevator.Requests, r => r is ServiceInstallRequest);
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
    public async Task Network_change_asks_for_firewall()
    {
        var vm = await LoadedAsync();

        vm.PrivateNetworksOnly = false;
        await vm.ApplyNetworkCommand.ExecuteAsync(null);

        Assert.Equal(new NetworkAccess(true, false), _api.Saved?.Network);
        var request = Assert.IsType<FirewallSetRequest>(Assert.Single(_elevator.Requests));
        Assert.Equal([80, 443], request.Ports);
        Assert.Equal(FirewallScope.Internet, request.Scope);
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
