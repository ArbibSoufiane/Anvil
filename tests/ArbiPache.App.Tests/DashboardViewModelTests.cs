using ArbiPache.App.Services;
using ArbiPache.App.Tests.Support;
using ArbiPache.App.ViewModels;
using ArbiPache.Core.Agent;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Preflight;
using ArbiPache.Core.Processes;
using ArbiPache.Core.Settings;

namespace ArbiPache.App.Tests;

public sealed class DashboardViewModelTests
{
    private static readonly AppPaths Paths = new(@"D:\ArbiPache");
    private readonly FakeAgentApi _api = new();

    private DashboardViewModel Make() => new(_api, Paths, new EditorLauncher(() => "auto", new NoRegistry(), _ => false, ""), ui: a => a());

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
        Assert.True(banner.IsBlocking);
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
    public async Task Mode_card_follows_status()
    {
        var vm = Make();
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        _api.Raise(_api.Status with { Mode = ServerMode.Production });

        Assert.Equal("Production", vm.ModeName);
        Assert.Contains("Errors logged, never shown; version headers hidden", vm.ModeLines);
    }
}
