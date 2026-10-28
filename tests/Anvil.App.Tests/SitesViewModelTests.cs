using Anvil.App.Tests.Support;
using Anvil.App.ViewModels;
using Anvil.Core.Agent;
using Anvil.Core.Elevation;
using Anvil.Core.Paths;
using Anvil.Core.Php;
using Anvil.Core.Processes;
using Anvil.Core.Sites;

namespace Anvil.App.Tests;

public sealed class SitesViewModelTests
{
    private static readonly AppPaths Paths = new(@"D:\Anvil");
    private readonly FakeAgentApi _api = new();
    private readonly FakeElevator _elevator = new();
    private readonly FakeCa _ca = new();
    private readonly FakeFolderPicker _picker = new();

    public SitesViewModelTests()
    {
        _api.Php.Add(new PhpVersionInfo("8.5.5", "vs17", 4, 1000, 9100, ["htdocs"], ProcessState.Running, IsDefault: true, EndOfLife: false));
        _api.Settings = _api.Settings with { DefaultPhp = "8.5.5" };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SitesViewModel Make() => new(_api, _elevator, _ca, _picker, Paths, () => ["192.168.10.2"], () => "192.168.10.2", ui: a => a());

    private static SiteDefinition Site(string name, SiteKind kind = SiteKind.Domain) =>
        new() { Id = SiteSet.IdFor(kind, name), Kind = kind, Type = SiteType.Php, Name = name, Root = $@"D:\Sites\{name}" };

    // Without a certificate choice the form's default applies: a local development certificate in Development mode.
    private async Task<SitesViewModel> AddShopAsync(CertSource? cert = null)
    {
        var vm = Make();
        await vm.LoadAsync(Ct);
        await vm.AddCommand.ExecuteAsync(null);
        var editor = vm.Editor!;
        editor.Name = "shop.test";
        if (cert is { } c) editor.Cert = c;
        await editor.SaveCommand.ExecuteAsync(null);
        return vm;
    }

    [Fact]
    public async Task Rows_describe_each_site()
    {
        _api.Sites.Add(Site("shop.test") with { Aliases = ["www.shop.test"], Cert = CertSource.LocalDev, Access = AccessLevel.ThisPc });
        _api.Sites.Add(Site("192.168.10.2", SiteKind.Ip) with
        {
            Mounts = [new SiteMount("/demo", @"D:\Sites\demo", SiteType.Php, null)], Cert = CertSource.LoadBalancer, Enabled = false,
        });
        _api.Htdocs.Add(new HtdocsEntry("blog", @"D:\Anvil\htdocs\blog", false, null, AccessLevel.Network));
        var vm = Make();

        await vm.LoadAsync(Ct);

        Assert.Equal("Sites · 2", vm.SitesTitle);
        Assert.Equal("htdocs folders · 1", vm.HtdocsTitle);
        var shop = vm.Sites.Single(r => r.Name == "shop.test");
        Assert.Equal("www.shop.test", shop.Detail);
        Assert.Equal("8.5.5 (default)", shop.Php);
        Assert.Equal("Local development", shop.Certificate);
        Assert.Equal("This PC only", shop.AccessText);
        Assert.Equal("Enabled", shop.StatusText);
        Assert.Equal("https://shop.test", shop.Url);
        var ip = vm.Sites.Single(r => r.Name == "192.168.10.2");
        Assert.Equal("IP site · single site mode · /demo", ip.Detail);
        Assert.Equal("HTTP only · client load balancer", ip.Certificate);
        Assert.Equal("Disabled", ip.StatusText);
        var blog = Assert.Single(vm.Htdocs);
        Assert.Equal("localhost/blog", blog.Local);
        Assert.Equal("192.168.10.2/blog", blog.Network);
    }

    [Fact]
    public async Task Search_filters_rows()
    {
        _api.Sites.Add(Site("shop.test"));
        _api.Sites.Add(Site("blog.test"));
        var vm = Make();
        await vm.LoadAsync(Ct);

        vm.SearchText = "sho";

        Assert.Equal(["shop.test"], vm.VisibleSites.Select(r => r.Name));
    }

    [Fact]
    public async Task Hosts_change_triggers_elevator()
    {
        _api.NextResult = ApplyResult.Success() with { HostsDomains = ["shop.test"] };

        var vm = await AddShopAsync(CertSource.None);

        var request = Assert.IsType<HostsSetRequest>(Assert.Single(_elevator.Requests));
        Assert.Equal(["shop.test"], request.Domains);
        Assert.Null(vm.Editor);
        Assert.True(vm.MessageOk);
        Assert.Contains(vm.Sites, r => r.Name == "shop.test");
    }

    [Fact]
    public async Task No_hosts_change_means_no_uac_prompt()
    {
        await AddShopAsync(CertSource.None);

        Assert.Empty(_elevator.Requests);
    }

    [Fact]
    public async Task Declined_uac_shows_message_and_keeps_site()
    {
        _api.NextResult = ApplyResult.Success() with { HostsDomains = ["shop.test"] };
        _elevator.Next = new ElevatedResult(false, "Permission was not granted.");

        var vm = await AddShopAsync(CertSource.None);

        Assert.False(vm.MessageOk);
        Assert.Contains("hosts file", vm.Message);
        Assert.Contains("Permission was not granted.", vm.Message);
        Assert.Contains(vm.Sites, r => r.Name == "shop.test");
        Assert.DoesNotContain(_api.Calls, c => c.StartsWith("remove", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Required_firewall_asks_elevator_for_both_ports()
    {
        _api.NextResult = ApplyResult.Success() with { RequiredFirewall = FirewallScope.LocalNetwork };

        await AddShopAsync(CertSource.None);

        var request = Assert.IsType<FirewallSetRequest>(Assert.Single(_elevator.Requests));
        Assert.Equal([80, 443], request.Ports);
        Assert.Equal(FirewallScope.LocalNetwork, request.Scope);
    }

    [Fact]
    public async Task First_local_dev_site_offers_to_trust_the_ca()
    {
        var vm = await AddShopAsync();
        Assert.True(vm.OfferTrust);

        vm.TrustCommand.Execute(null);

        Assert.Equal(1, _ca.TrustCalls);
        Assert.False(vm.OfferTrust);
    }

    [Fact]
    public async Task Trusted_ca_is_not_offered_again()
    {
        _ca.Trusted = true;

        var vm = await AddShopAsync();

        Assert.False(vm.OfferTrust);
    }

    [Fact]
    public async Task Toggle_and_remove_call_the_agent()
    {
        _api.Sites.Add(Site("shop.test"));
        var vm = Make();
        await vm.LoadAsync(Ct);

        await vm.ToggleCommand.ExecuteAsync(vm.Sites.Single());
        var row = vm.Sites.Single();
        row.ConfirmingRemove = true;
        await vm.RemoveCommand.ExecuteAsync(row);

        Assert.Contains("site shop.test off", _api.Calls);
        Assert.Contains("remove site shop.test", _api.Calls);
        Assert.Empty(vm.Sites);
    }

    [Fact]
    public async Task Remove_asks_first()
    {
        _api.Sites.Add(Site("shop.test"));
        var vm = Make();
        await vm.LoadAsync(Ct);
        var row = vm.Sites.Single();

        await vm.RemoveCommand.ExecuteAsync(row);

        Assert.True(row.ConfirmingRemove);
        Assert.DoesNotContain(_api.Calls, c => c.StartsWith("remove", StringComparison.Ordinal));
    }
}
