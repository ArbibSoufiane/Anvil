using ArbiPache.App.Tests.Support;
using ArbiPache.App.ViewModels;
using ArbiPache.Core.Agent;
using ArbiPache.Core.Elevation;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Settings;
using ArbiPache.Core.Sites;

namespace ArbiPache.App.Tests;

public sealed class SettingsViewModelTests
{
    private static readonly AppPaths Paths = new(@"D:\ArbiPache");
    private readonly FakeAgentApi _api = new();
    private readonly FakeElevator _elevator = new();
    private readonly FakeRunKey _runKey = new();
    private readonly FakeCa _ca = new();
    private readonly FakeFolderPicker _picker = new();
    private readonly List<AppSettings> _changed = [];
    private int _sitesChanged;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<SettingsViewModel> LoadedAsync()
    {
        var vm = new SettingsViewModel(_api, _elevator, _runKey, _ca, _picker, Paths, () => "192.168.10.2", _changed.Add, () => _sitesChanged++);
        await vm.LoadAsync(Ct);
        return vm;
    }

    [Fact]
    public async Task StartWithWindows_sets_run_key_with_minimized()
    {
        var vm = await LoadedAsync();

        vm.StartWithWindows = true;
        await vm.ApplyGeneralCommand.ExecuteAsync(null);

        Assert.Equal("\"D:\\ArbiPache\\ArbiPache.exe\" --minimized", _runKey.Command);
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

        Assert.Equal(@"C:\Tools\subl.exe", _api.Saved?.Editor);
        Assert.Equal(@"C:\Tools\subl.exe", vm.EditorText);
    }
}
