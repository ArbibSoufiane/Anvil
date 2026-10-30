using Anvil.App.Services;
using Anvil.App.Tests.Support;
using Anvil.App.ViewModels;
using Anvil.Core;
using Anvil.Core.Agent;
using Anvil.Core.Elevation;
using Anvil.Core.Network;
using Anvil.Core.Paths;
using Anvil.Core.Preflight;
using Anvil.Core.Processes;
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
    private readonly FakeTextClipboard _clipboard = new();
    private IReadOnlyList<LanAddress> _lan = [Lan("Ethernet", "192.168.100.254")];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<SettingsViewModel> LoadedAsync()
    {
        var vm = new SettingsViewModel(_api, _elevator, _runKey, _ca, _picker, Paths, () => _lan, _clipboard, _changed.Add, () => _sitesChanged++,
            _editor, ui: a => a());
        await vm.LoadAsync(Ct);
        return vm;
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
    public async Task Run_apache_when_windows_starts_turns_on_both_switches()
    {
        _api.Settings = _api.Settings with { StartServicesOnLaunch = false, StartWithWindows = false };
        var vm = await LoadedAsync();
        Assert.False(vm.RunApacheAtLogin);

        vm.RunApacheAtLogin = true;
        await vm.ApplyRunApacheAtLoginCommand.ExecuteAsync(null);

        Assert.True(vm.StartWithWindows);
        Assert.True(vm.StartServicesOnLaunch);
        Assert.Equal("\"D:\\Anvil\\Anvil.exe\" --minimized", _runKey.Command);
        Assert.True(_api.Saved?.StartWithWindows);
        Assert.True(_api.Saved?.StartServicesOnLaunch);

        vm.RunApacheAtLogin = false;
        await vm.ApplyRunApacheAtLoginCommand.ExecuteAsync(null);

        Assert.False(vm.StartWithWindows);
        Assert.True(vm.StartServicesOnLaunch);
        Assert.Null(_runKey.Command);
    }

    [Fact]
    public async Task Run_apache_when_windows_starts_follows_the_other_switches()
    {
        _api.Settings = _api.Settings with { StartServicesOnLaunch = true, StartWithWindows = true };
        var vm = await LoadedAsync();
        Assert.True(vm.RunApacheAtLogin);

        vm.StartServicesOnLaunch = false;
        await vm.ApplyGeneralCommand.ExecuteAsync(null);

        Assert.False(vm.RunApacheAtLogin);
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

    private static LanAddress Lan(string adapter, string address) => new("{" + adapter.ToUpperInvariant() + "}", adapter, System.Net.IPAddress.Parse(address));

    private AgentStatus Status(bool apacheRunning = true, IReadOnlyList<string>? nameService = null) => _api.Status with
    {
        Services = [new ServiceStatus(ServiceId.Apache, apacheRunning ? ProcessState.Running : ProcessState.Stopped, "2.4.69", null)],
        NameServiceAddresses = nameService ?? [],
    };

    [Fact]
    public async Task Shows_the_colleague_commands_for_the_domain_and_address()
    {
        _api.Settings = _api.Settings with { NetworkDomain = "arbib.pc" };

        var vm = await LoadedAsync();

        Assert.Equal("arbib.pc", vm.NetworkDomain);
        Assert.Equal("192.168.100.254", vm.ThisPcAddress);
        Assert.Equal("Add-DnsClientNrptRule -Namespace \".arbib.pc\" -NameServers 192.168.100.254", vm.WindowsCommand);
        Assert.Equal("sudo mkdir -p /etc/resolver && echo \"nameserver 192.168.100.254\" | sudo tee /etc/resolver/arbib.pc", vm.MacCommand);
    }

    [Fact]
    public async Task Copy_puts_the_commands_on_the_clipboard()
    {
        _api.Settings = _api.Settings with { NetworkDomain = "arbib.pc" };
        var vm = await LoadedAsync();

        await vm.CopyWindowsCommandCommand.ExecuteAsync(null);
        Assert.Equal(vm.WindowsCommand, _clipboard.Text);

        await vm.CopyMacCommandCommand.ExecuteAsync(null);
        Assert.Equal(vm.MacCommand, _clipboard.Text);
    }

    [Fact]
    public async Task No_domain_or_no_address_hides_the_commands()
    {
        var vm = await LoadedAsync();
        Assert.Null(vm.WindowsCommand);
        Assert.Null(vm.MacCommand);

        _api.Settings = _api.Settings with { NetworkDomain = "arbib.pc" };
        _lan = [];
        await vm.LoadAsync(Ct);

        Assert.Null(vm.WindowsCommand);
        Assert.Null(vm.ThisPcAddress);
        Assert.Equal("This PC has no address on a local network.", vm.NameServiceStatus);
    }

    [Fact]
    public async Task Commands_follow_the_current_address()
    {
        _api.Settings = _api.Settings with { NetworkDomain = "arbib.pc" };
        var vm = await LoadedAsync();
        _lan = [Lan("Wi-Fi", "192.168.1.40")];

        _api.Raise(Status(nameService: ["192.168.1.40"]));

        Assert.EndsWith("-NameServers 192.168.1.40", vm.WindowsCommand);
    }

    [Fact]
    public async Task The_htdocs_label_follows_the_current_address()
    {
        var vm = await LoadedAsync();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        _lan = [Lan("Wi-Fi", "192.168.1.40")];

        _api.Raise(Status());

        Assert.Contains(nameof(SettingsViewModel.NetworkLabel), changed);
        Assert.Equal("Let devices on my network open htdocs at 192.168.1.40", vm.NetworkLabel);
    }

    [Fact]
    public async Task The_address_the_name_service_answers_on_comes_first()
    {
        _api.Settings = _api.Settings with { NetworkDomain = "arbib.pc" };
        _lan = [Lan("Ethernet", "192.168.100.254"), Lan("Wi-Fi", "192.168.1.40")];
        var vm = await LoadedAsync();

        _api.Raise(Status(nameService: ["192.168.1.40"]));

        Assert.Equal("192.168.1.40", vm.ThisPcAddress);
    }

    [Theory]
    [InlineData(null, false, new string[0], false, "Off: set a network domain to turn it on.")]
    [InlineData("arbib.pc", false, new string[0], false, "Off while Apache is stopped.")]
    [InlineData("arbib.pc", true, new[] { "192.168.100.254" }, false, "Answering for *.arbib.pc on 192.168.100.254.")]
    [InlineData("arbib.pc", true, new string[0], true, "Off: port 53 is in use on every network address.")]
    // Apache reports itself starting before the name service has bound anything.
    [InlineData("arbib.pc", true, new string[0], false, "Starting with Apache.")]
    public async Task Name_service_status(string? domain, bool apacheRunning, string[] listening, bool portTaken, string text)
    {
        _api.Settings = _api.Settings with { NetworkDomain = domain };
        var vm = await LoadedAsync();

        _api.Raise(Status(apacheRunning, listening) with
        {
            Findings = portTaken ? [new PreflightFinding(NetworkFindings.DnsBindPrefix + "192.168.100.254", Severity.Warning, "Port 53 is in use.", null)] : [],
        });

        Assert.Equal(text, vm.NameServiceStatus);
    }

    [Fact]
    public async Task Invalid_domain_is_explained_and_not_saved()
    {
        var vm = await LoadedAsync();
        vm.NetworkDomain = "pc";

        await vm.SaveNetworkDomainCommand.ExecuteAsync(null);

        Assert.Equal("A network domain needs at least two parts, such as arbib.pc.", vm.Message);
        Assert.False(vm.MessageOk);
        Assert.Null(_api.Saved);
    }

    [Fact]
    public async Task Saving_a_domain_normalizes_it_and_opens_the_firewall()
    {
        var request = new FirewallSetRequest("2.4.69", [80, 443], true);
        _api.SaveResult = _api.SaveResult with { RequiredFirewall = request };
        var vm = await LoadedAsync();
        vm.NetworkDomain = " Arbib.PC. ";

        await vm.SaveNetworkDomainCommand.ExecuteAsync(null);

        Assert.Equal("arbib.pc", _api.Saved!.NetworkDomain);
        Assert.Equal("arbib.pc", vm.NetworkDomain);
        Assert.Equal(request, Assert.Single(_elevator.Requests));
        Assert.EndsWith("-NameServers 192.168.100.254", vm.WindowsCommand);
    }

    [Fact]
    public async Task Clearing_the_domain_turns_network_names_off()
    {
        _api.Settings = _api.Settings with { NetworkDomain = "arbib.pc" };
        var vm = await LoadedAsync();
        vm.NetworkDomain = "  ";

        await vm.SaveNetworkDomainCommand.ExecuteAsync(null);

        Assert.Null(_api.Saved!.NetworkDomain);
        Assert.Null(vm.WindowsCommand);
        Assert.Equal("Off: set a network domain to turn it on.", vm.NameServiceStatus);
    }

    [Fact]
    public async Task Public_network_and_busy_port_53_show_on_settings_too()
    {
        var vm = await LoadedAsync();

        _api.Raise(Status() with
        {
            Findings =
            [
                new PreflightFinding(NetworkFindings.FirewallId, Severity.Warning, "firewall", null),
                new PreflightFinding(NetworkFindings.PublicId, Severity.Warning, "public", null),
                new PreflightFinding(NetworkFindings.DnsBindPrefix + "192.168.100.254", Severity.Warning, "busy", null),
            ],
        });

        Assert.Equal(["public", "busy"], vm.NetworkProblems);
    }
}
