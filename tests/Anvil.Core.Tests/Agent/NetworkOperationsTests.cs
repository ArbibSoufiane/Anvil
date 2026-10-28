using System.Net;
using Anvil.Agent;
using Anvil.Core.Agent;
using Anvil.Core.Components;
using Anvil.Core.Elevation;
using Anvil.Core.IO;
using Anvil.Core.Network;
using Anvil.Core.Paths;
using Anvil.Core.Settings;
using Anvil.Core.Sites;

namespace Anvil.Core.Tests.Agent;

// The name service follows the web server and this PC's addresses; the firewall and network findings follow what is
// open. Every system seam is a fake, so nothing here touches the firewall, the network or port 53.
public sealed class NetworkOperationsTests : IAsyncLifetime
{
    private const string ActiveApache = "2.4.69";
    private const string OtherApache = "2.4.70";

    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ap-netops-" + Guid.NewGuid().ToString("N")));
    private readonly List<string> _events = [];
    private readonly FakeNameService _names = new();
    private readonly FakeApache _apache;
    private IReadOnlyList<LanAddress> _lan = [Lan("Ethernet")];
    private IReadOnlyList<ExistingFirewallRule> _firewall = [];
    private Orchestrator _o = null!;

    public NetworkOperationsTests() => _apache = new FakeApache(_events);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        InstallApache(ActiveApache);
        InstallApache(OtherApache);
        Directory.CreateDirectory(_paths.Data);
        new SettingsStore(_paths).Save(new AppSettings { ActiveApache = ActiveApache, StartServicesOnLaunch = false });
        _o = new Orchestrator(_paths, new SettingsStore(_paths), new FakeTester(), _apache, _ => [],
            new ComponentCatalog([], new ShippedCatalogProvider(_paths)),
            new ComponentInstaller(_paths, new Downloader(new HttpClient()), _ => null), new FakePools(_events), _ => true,
            hostsText: () => "", legacyHome: Path.Combine(_paths.Root, "v3"),
            names: _names, lanAddresses: () => _lan, publicAdapters: () => new HashSet<string>(), firewallRules: () => _firewall);
        await _o.InitializeAsync(Ct);
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_paths.Root, recursive: true); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    private static LanAddress Lan(string adapter, string address = "192.168.100.254") =>
        new("{" + adapter.ToUpperInvariant() + "}", adapter, IPAddress.Parse(address));

    private static IPAddress Ip(string address) => IPAddress.Parse(address);

    private void InstallApache(string version)
    {
        var runtime = _paths.ApacheRuntime(version);
        Directory.CreateDirectory(Path.Combine(runtime, "conf"));
        Directory.CreateDirectory(Path.Combine(runtime, "bin"));
        File.WriteAllText(Path.Combine(runtime, "conf", "httpd.conf"), "ServerRoot \"c:/Apache24\"\r\nListen 80\r\n");
        File.WriteAllText(Path.Combine(runtime, "bin", "httpd.exe"), "");
    }

    private async Task Settings(Func<AppSettings, AppSettings> change) =>
        Assert.True((await _o.SaveSettingsAsync(change(_o.Settings), Ct)).Ok);

    private SiteDefinition NewSite(string name, AccessLevel access)
    {
        var root = Path.Combine(_paths.Root, "projects", name);
        Directory.CreateDirectory(root);
        return new SiteDefinition
        {
            Id = SiteSet.IdFor(SiteKind.Domain, name), Kind = SiteKind.Domain, Type = SiteType.Php, Name = name, Root = root, Access = access,
        };
    }

    private IReadOnlyList<ExistingFirewallRule> ExactRulesFor(FirewallSetRequest request) =>
        FirewallRulePlanner.Plan(request, _paths)
            .Select(r => new ExistingFirewallRule(r.Name, r.Program, (int)r.Protocol, r.LocalPorts, r.Profiles, r.RemoteAddresses, true, true))
            .ToList();

    [Fact]
    public async Task Starting_the_web_server_starts_the_name_service()
    {
        await Settings(s => s with { NetworkDomain = "arbib.pc" });

        await _o.StartAsync(ServiceId.Apache, Ct);

        Assert.Equal("arbib.pc", _names.LastCall.Domain);
        Assert.Equal([Ip("192.168.100.254")], _names.LastCall.Addresses);
    }

    [Fact]
    public async Task Stopping_the_web_server_stops_the_name_service()
    {
        await Settings(s => s with { NetworkDomain = "arbib.pc" });
        await _o.StartAsync(ServiceId.Apache, Ct);

        await _o.StopAsync(ServiceId.Apache, Ct);

        Assert.Null(_names.LastCall.Domain);
    }

    [Fact]
    public async Task A_web_server_that_gave_up_after_crashing_stops_the_name_service()
    {
        await Settings(s => s with { NetworkDomain = "arbib.pc" });
        await _o.StartAsync(ServiceId.Apache, Ct);

        _apache.GiveUp(); // no operation runs: the supervisor stopped restarting it

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_names.LastCall.Domain is not null && DateTime.UtcNow < deadline) await Task.Delay(20, Ct);
        Assert.Null(_names.LastCall.Domain);
        Assert.Empty(_o.Status().NameServiceAddresses);
    }

    [Fact]
    public async Task Address_change_moves_the_name_service()
    {
        await Settings(s => s with { NetworkDomain = "arbib.pc" });
        await _o.StartAsync(ServiceId.Apache, Ct);
        _lan = [Lan("Wi-Fi", "192.168.1.40")];

        await _o.RefreshNetworkAsync(Ct);

        Assert.Equal([Ip("192.168.1.40")], _names.LastCall.Addresses);
        Assert.Equal(["192.168.1.40"], _o.Status().NameServiceAddresses);
    }

    [Fact]
    public async Task Bind_failures_become_findings()
    {
        _names.FailOn = Ip("192.168.100.254");
        await Settings(s => s with { NetworkDomain = "arbib.pc" });

        var started = await _o.StartAsync(ServiceId.Apache, Ct);

        Assert.True(started.Ok); // a port-53 clash never stops the web server
        Assert.Contains(_o.Status().Findings, f => f.Id == "dns-bind-192.168.100.254");
    }

    [Fact]
    public async Task Refresh_tries_a_busy_address_again()
    {
        _names.FailOn = Ip("192.168.100.254");
        await Settings(s => s with { NetworkDomain = "arbib.pc" });
        await _o.StartAsync(ServiceId.Apache, Ct);
        _names.FailOn = null;

        await _o.RefreshNetworkAsync(Ct);

        Assert.DoesNotContain(_o.Status().Findings, f => f.Id.StartsWith(NetworkFindings.DnsBindPrefix, StringComparison.Ordinal));
        Assert.Equal(["192.168.100.254"], _o.Status().NameServiceAddresses);
    }

    [Fact]
    public async Task Saving_a_network_site_asks_for_the_firewall_when_rules_are_missing()
    {
        _firewall = [];

        var r = await _o.SaveSiteAsync(NewSite("shop.test", AccessLevel.Network), Ct);

        Assert.Equal(_o.FirewallRequest(), r.RequiredFirewall);
        Assert.NotNull(r.RequiredFirewall);
    }

    [Fact]
    public async Task Saving_when_the_rules_are_in_place_asks_nothing()
    {
        _firewall = ExactRulesFor(_o.FirewallRequest() ?? new FirewallSetRequest(ActiveApache, [80, 443], false));

        Assert.Null((await _o.SaveSiteAsync(NewSite("shop.test", AccessLevel.Network), Ct)).RequiredFirewall);
        Assert.Null(_o.FirewallRequest());
        Assert.DoesNotContain(_o.Status().Findings, f => f.Id == NetworkFindings.FirewallId);
    }

    [Fact]
    public async Task Saving_a_domain_asks_for_the_name_service_rule()
    {
        _firewall = ExactRulesFor(new FirewallSetRequest(ActiveApache, [80, 443], false));

        var r = await _o.SaveSettingsAsync(_o.Settings with { NetworkDomain = "arbib.pc" }, Ct);

        Assert.Equal(new FirewallSetRequest(ActiveApache, [80, 443], true), r.RequiredFirewall);
    }

    [Fact]
    public async Task Switching_apache_shows_the_firewall_finding_without_asking()
    {
        _firewall = ExactRulesFor(new FirewallSetRequest(ActiveApache, [80, 443], false));
        await Settings(s => s with { Network = new(true) });

        var r = await _o.ActivateAsync(ComponentKind.Apache, OtherApache, Ct);

        Assert.Null(r.RequiredFirewall);
        Assert.Contains(_o.Status().Findings, f => f.Id == NetworkFindings.FirewallId);
    }

    [Fact]
    public async Task Nothing_on_the_network_reports_nothing()
    {
        await Settings(s => s with { Network = new(false) });

        Assert.Null(_o.FirewallRequest());
        Assert.Empty(_o.Status().Findings);
    }
}
