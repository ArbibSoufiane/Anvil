using Anvil.App.Services;
using Anvil.App.Tests.Support;
using Anvil.App.ViewModels;
using Anvil.Core.Agent;
using Anvil.Core.Database;
using Anvil.Core.Elevation;
using Anvil.Core.Paths;
using Anvil.Core.Preflight;
using Anvil.Core.Processes;
using Anvil.Core.Settings;

namespace Anvil.App.Tests;

public sealed class DashboardViewModelTests
{
    private static readonly AppPaths Paths = new(@"D:\Anvil");
    private readonly FakeAgentApi _api = new();
    private readonly FakeElevator _elevator = new();
    private readonly List<string> _opened = [];

    private DashboardViewModel Make() => new(_api, Paths, new EditorLauncher(() => "auto", new NoRegistry(), _ => false, ""), ui: a => a(),
        elevator: _elevator, openUrl: _opened.Add);

    private AgentStatus StatusWith(params PreflightFinding[] findings) => _api.Status with { Findings = findings };

    [Fact]
    public async Task Apache_card_shows_running_from_status_event()
    {
        var vm = Make();
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Stopped", vm.ApacheStateText);
        Assert.Equal("Start", vm.StartStopLabel);

        _api.Raise(_api.Status with { Services = [new ServiceStatus(ServiceId.Apache, ProcessState.Running, "2.4.69", null)] });

        Assert.Equal("Running", vm.ApacheStateText);
        Assert.True(vm.ApacheRunning);
        Assert.Equal("Stop", vm.StartStopLabel);
        Assert.Equal("2.4.69", vm.ApacheVersion);
        Assert.Equal("WinNT MPM · 256 threads", vm.ApacheWorkers);
        Assert.Equal("80 · 443", vm.ApachePorts);
    }

    [Fact]
    public async Task Php_card_shows_default_version_and_pools()
    {
        var vm = Make();
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Not installed", vm.PhpStateText);
        Assert.Equal("Install PHP on the PHP page", vm.PhpDefault);

        _api.Raise(_api.Status with
        {
            Services =
            [
                new ServiceStatus(ServiceId.Apache, ProcessState.Running, "2.4.69", null),
                new ServiceStatus(ServiceId.Php, ProcessState.Running, "8.5.12", "8.5 ×4"),
            ],
        });

        Assert.Equal("Running", vm.PhpStateText);
        Assert.True(vm.PhpRunning);
        Assert.Equal("PHP 8.5.12 NTS x64", vm.PhpDefault);
        Assert.Equal("8.5 ×4", vm.PhpPools);
    }

    [Fact]
    public async Task Stopped_php_pools_say_when_they_start()
    {
        _api.Status = _api.Status with { Services = [new ServiceStatus(ServiceId.Php, ProcessState.Stopped, "8.5.12", null)] };
        var vm = Make();

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Stopped", vm.PhpStateText);
        Assert.Equal("Start with Apache", vm.PhpPools);
    }

    [Fact]
    public async Task Restart_pools_restarts_php_only()
    {
        var vm = Make();
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.RestartPhpCommand.ExecuteAsync(null);

        Assert.Equal(["restart Php"], _api.Calls);
    }

    [Fact]
    public async Task Dashboard_shows_port_conflict_finding()
    {
        _api.Status = _api.Status with
        {
            Findings = [new PreflightFinding("port-80", Severity.Blocking, "Port 80 is used by Skype (PID 7).", "Close Skype, or change the HTTP port in Settings.")],
        };
        var vm = Make();

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(vm.HasFindings);
        var banner = Assert.Single(vm.Findings);
        Assert.Equal("Port 80 is used by Skype (PID 7).", banner.Message);
        Assert.Equal("Close Skype, or change the HTTP port in Settings.", banner.Fix);
        Assert.True(banner.Blocking);
    }

    [Fact]
    public async Task Start_failure_is_shown_on_the_card()
    {
        _api.NextResult = ApplyResult.Failure("Install Apache first, on the Apache page.");
        var vm = Make();
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.StartStopApacheCommand.ExecuteAsync(null);

        Assert.Equal(["start Apache"], _api.Calls);
        Assert.Equal("Install Apache first, on the Apache page.", vm.ApacheMessage);
    }

    [Fact]
    public async Task Stop_when_running_calls_stop()
    {
        _api.Status = _api.Status with { Services = [new ServiceStatus(ServiceId.Apache, ProcessState.Running, "2.4.69", null)] };
        var vm = Make();
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.StartStopApacheCommand.ExecuteAsync(null);

        Assert.Equal(["stop Apache"], _api.Calls);
        Assert.Null(vm.ApacheMessage);
    }

    [Fact]
    public async Task Local_url_includes_a_non_default_port()
    {
        _api.Settings = _api.Settings with { HttpPort = 8080 };
        var vm = Make();

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("http://localhost:8080/", vm.LocalUrl);
    }

    [Fact]
    public void A_closed_warning_stays_closed_until_it_comes_back()
    {
        var vm = Make();
        var publicNetwork = new PreflightFinding("network-public", Severity.Warning, "Windows treats Wi-Fi as Public.", "f");
        var firewall = new PreflightFinding("firewall-missing", Severity.Warning, "m", "f");
        _api.Raise(StatusWith(publicNetwork, firewall));

        var item = vm.Findings.First();
        Assert.True(item.CanDismiss);
        vm.DismissFindingCommand.Execute(item);

        Assert.Equal(["firewall-missing"], vm.Findings.Select(f => f.Id));
        _api.Raise(StatusWith(publicNetwork, firewall));
        Assert.Equal(["firewall-missing"], vm.Findings.Select(f => f.Id));

        // Once the problem is gone, finding it again shows the warning again.
        _api.Raise(StatusWith(firewall));
        _api.Raise(StatusWith(publicNetwork, firewall));
        Assert.Equal(["network-public", "firewall-missing"], vm.Findings.Select(f => f.Id));
    }

    [Fact]
    public void Closing_the_last_warning_hides_the_banners_and_blocking_problems_cannot_be_closed()
    {
        var vm = Make();
        _api.Raise(StatusWith(new PreflightFinding("firewall-missing", Severity.Warning, "m", "f")));

        vm.DismissFindingCommand.Execute(vm.Findings.Single());

        Assert.False(vm.HasFindings);
        _api.Raise(StatusWith(new PreflightFinding("port-80", Severity.Blocking, "Port 80 is used by Skype (PID 7).", "Close Skype.")));
        Assert.False(vm.Findings.Single().CanDismiss);
    }

    [Fact]
    public async Task Firewall_banner_button_runs_the_agents_request_then_refreshes()
    {
        var vm = Make();
        _api.FirewallRequest = new FirewallSetRequest("2.4.69", [80, 443], true);
        _api.Raise(StatusWith(new PreflightFinding("firewall-missing", Severity.Warning, "m", "f")));
        var item = vm.Findings.Single();
        Assert.Equal("Open firewall", item.ActionLabel);

        await vm.RunFindingActionCommand.ExecuteAsync(item);

        Assert.Equal(_api.FirewallRequest, Assert.Single(_elevator.Requests));
        Assert.Equal(1, _api.RefreshNetworkCalls);
    }

    [Fact]
    public async Task Refused_firewall_prompt_is_explained()
    {
        var vm = Make();
        _api.FirewallRequest = new FirewallSetRequest("2.4.69", [80, 443], true);
        _elevator.Next = new ElevatedResult(false, "The operation was canceled by the user.");
        _api.Raise(StatusWith(new PreflightFinding("firewall-missing", Severity.Warning, "m", "f")));

        await vm.RunFindingActionCommand.ExecuteAsync(vm.Findings.Single());

        Assert.StartsWith("Windows did not let Anvil open the firewall", vm.FindingMessage);
    }

    [Fact]
    public async Task Public_network_banner_opens_windows_settings()
    {
        var vm = Make();
        _api.Raise(StatusWith(new PreflightFinding("network-public", Severity.Warning, "m", "f")));
        Assert.Equal("Open network settings", vm.Findings.Single().ActionLabel);

        await vm.RunFindingActionCommand.ExecuteAsync(vm.Findings.Single());

        Assert.Equal("ms-settings:network-status", _opened.Single());
    }

    [Fact]
    public void Other_findings_have_no_button()
    {
        var vm = Make();

        _api.Raise(StatusWith(new PreflightFinding("port-80", Severity.Blocking, "Port 80 is used by Skype (PID 7).", "Close Skype.")));

        var item = vm.Findings.Single();
        Assert.Null(item.ActionLabel);
        Assert.False(item.HasAction);
        Assert.True(item.Blocking);
    }
}
