using ArbiPache.App.Services;
using ArbiPache.App.Tests.Support;
using ArbiPache.Core.Agent;
using ArbiPache.Core.Elevation;
using ArbiPache.Core.Paths;

namespace ArbiPache.App.Tests;

/// <summary>Moving the Agent between the window's child process and the ArbiPacheAgent Windows service.</summary>
public sealed class ServiceToggleTests
{
    private const string Me = "S-1-5-21-1004336348-1177238915-682003330-1001";
    private static readonly AppPaths Paths = new(@"D:\ArbiPache");

    private readonly FakeAgentApi _api = new();
    private readonly FakeElevator _elevator;
    private readonly FakeAgentLink _link;
    private readonly FakeAgentService _service;

    public ServiceToggleTests()
    {
        _elevator = new FakeElevator { Calls = _api.Calls };
        _link = new FakeAgentLink(_api.Calls, () => _api.Settings.RunAsService ? "as service" : "as child");
        _service = new FakeAgentService(_api.Calls);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ServiceSwitch Switch(string? folderProblem = null) => new(_api, _link, _elevator, _service, Paths, Me, () => folderProblem);

    // The service install refuses such a folder anyway; asking first keeps the sites up and spares two UAC prompts.
    [Fact]
    public async Task Toggle_on_refuses_a_folder_others_can_change_before_stopping_anything()
    {
        const string open = @"Authenticated Users can change files in D:\ArbiPache, so it could replace ArbiPache.Agent.exe and run code as the service.";

        var problem = await Switch(open).TurnOnAsync(Ct);

        Assert.Equal(open + " ArbiPache keeps running as before, without the service.", problem);
        Assert.Empty(_api.Calls);
        Assert.Empty(_elevator.Requests);
        Assert.False(_api.Settings.RunAsService);
    }

    [Fact]
    public async Task Toggle_on_stops_child_installs_service_and_reconnects()
    {
        var problem = await Switch().TurnOnAsync(Ct);

        Assert.Null(problem);
        Assert.Equal(["hold as service", "shutdown True", "gone", "acl-grant", "service-install", "start", "release", "connect"], _api.Calls);
        Assert.True(_api.Settings.RunAsService);
        Assert.Equal([Me], _api.Settings.ControlUserSids);
        Assert.Equal(AclPlanner.Plan(Paths, [], null), Assert.IsType<AclGrantRequest>(_elevator.Requests[0]).Grants);
        Assert.Equal(new ServiceInstallRequest(@"D:\ArbiPache\ArbiPache.Agent.exe", Me), _elevator.Requests[1]);
    }

    [Fact]
    public async Task Folder_access_the_service_needs_goes_to_the_elevator()
    {
        AclGrant[] grants = [new(@"D:\Sites\shop", AclRight.Read)];

        var problem = await new ElevationFollowUp(_elevator).RunAsync(ApplyResult.Success() with { RequiredGrants = grants }, 80, 443, Ct);

        Assert.Null(problem);
        Assert.Equal(grants, Assert.IsType<AclGrantRequest>(Assert.Single(_elevator.Requests)).Grants);
    }

    [Fact]
    public async Task Refused_folder_access_says_the_site_will_not_open()
    {
        _elevator.Next = new ElevatedResult(false, "The operation was canceled by the user.");

        var problem = await new ElevationFollowUp(_elevator).RunAsync(
            ApplyResult.Success() with { RequiredGrants = [new(@"D:\Sites\shop", AclRight.Read), new(@"D:\Sites\shop\storage", AclRight.Modify)] }, 80, 443, Ct);

        Assert.Equal(@"Windows did not let ArbiPache give its service access to D:\Sites\shop and D:\Sites\shop\storage (The operation was "
            + "canceled by the user.). Those sites show an error until it does: save the site again to retry.", problem);
    }

    [Fact]
    public async Task Toggle_off_uninstalls_and_starts_child()
    {
        _api.Settings = _api.Settings with { RunAsService = true, ControlUserSids = [Me] };

        var problem = await Switch().TurnOffAsync(Ct);

        Assert.Null(problem);
        Assert.Equal(["hold as service", "service-uninstall", "gone", "removed", "release", "connect"], _api.Calls);
        Assert.False(_api.Settings.RunAsService);
        // The user stays allowed on the pipe, so the window can reach a service that is turned on again later.
        Assert.Equal([Me], _api.Settings.ControlUserSids);
    }

    [Fact]
    public async Task A_refused_install_brings_the_child_back_with_the_old_settings()
    {
        _elevator.ByVerb["service-install"] = new ElevatedResult(false, "The operation was canceled by the user.");
        var before = _api.Settings;

        var problem = await Switch().TurnOnAsync(Ct);

        Assert.Equal("Windows did not let ArbiPache install its service (The operation was canceled by the user.). "
            + "ArbiPache keeps running as before, without the service.", problem);
        Assert.Equal(["hold as service", "shutdown True", "gone", "acl-grant", "service-install", "release", "connect"], _api.Calls);
        Assert.Equal(before, _api.Settings);
    }

    [Fact]
    public async Task Refused_folder_access_installs_nothing()
    {
        _elevator.ByVerb["acl-grant"] = new ElevatedResult(false, @"D:\Sites\shop is reached through a link to another folder; ArbiPache did not change its permissions.");

        var problem = await Switch().TurnOnAsync(Ct);

        Assert.Equal(@"Windows did not let ArbiPache give its service access to the site folders (D:\Sites\shop is reached through a link to another "
            + "folder; ArbiPache did not change its permissions.). ArbiPache keeps running as before, without the service.", problem);
        Assert.DoesNotContain("service-install", _api.Calls);
        Assert.False(_api.Settings.RunAsService);
    }

    [Fact]
    public async Task A_service_that_does_not_start_is_removed_again()
    {
        _service.StartFails = new InvalidOperationException("Cannot start service ArbiPacheAgent on computer '.'.");

        var problem = await Switch().TurnOnAsync(Ct);

        Assert.Equal("The ArbiPache service did not start (Cannot start service ArbiPacheAgent on computer '.'.). "
            + "ArbiPache keeps running as before, without the service.", problem);
        Assert.Equal(["hold as service", "shutdown True", "gone", "acl-grant", "service-install", "start", "service-uninstall", "removed", "release", "connect"],
            _api.Calls);
        Assert.False(_api.Settings.RunAsService);
    }

    [Fact]
    public async Task A_service_the_window_cannot_reach_is_reported()
    {
        _link.ConnectFails = new TimeoutException("The operation has timed out.");

        var problem = await Switch().TurnOnAsync(Ct);

        Assert.Equal("ArbiPache now runs as a Windows service, but this window cannot reach it yet (The operation has timed out.). "
            + "It keeps trying.", problem);
        Assert.True(_api.Settings.RunAsService);
    }

    [Fact]
    public async Task Turning_off_when_windows_refuses_keeps_the_service()
    {
        _api.Settings = _api.Settings with { RunAsService = true, ControlUserSids = [Me] };
        _elevator.ByVerb["service-uninstall"] = new ElevatedResult(false, "The operation was canceled by the user.");

        var problem = await Switch().TurnOffAsync(Ct);

        Assert.Equal("Windows did not let ArbiPache remove its service (The operation was canceled by the user.). "
            + "ArbiPache keeps running as a Windows service.", problem);
        Assert.Equal(["hold as service", "service-uninstall", "release"], _api.Calls);
        Assert.True(_api.Settings.RunAsService);
    }
}
