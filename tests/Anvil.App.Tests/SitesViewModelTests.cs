using System.Net;
using Anvil.App.Tests.Support;
using Anvil.App.ViewModels;
using Anvil.Core.Agent;
using Anvil.Core.Elevation;
using Anvil.Core.Network;
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
    private readonly FakeTextClipboard _clipboard = new();
    private IReadOnlyList<LanAddress> _lan = [Lan("Ethernet", "192.168.10.2")];

    public SitesViewModelTests()
    {
        _api.Php.Add(new PhpVersionInfo("8.5.5", "vs17", 4, 1000, 9100, ["htdocs"], ProcessState.Running, IsDefault: true, EndOfLife: false));
        _api.Settings = _api.Settings with { DefaultPhp = "8.5.5" };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SitesViewModel Make() => new(_api, _elevator, _ca, _picker, Paths, () => _lan, _clipboard, ui: a => a());

    private static LanAddress Lan(string adapter, string address) => new("{" + adapter.ToUpperInvariant() + "}", adapter, IPAddress.Parse(address));

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
        _api.Sites.Add(Site("blog.test") with { Enabled = false });
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
        var plain = vm.Sites.Single(r => r.Name == "blog.test");
        Assert.Equal("", plain.Detail);
        Assert.Equal("HTTP only", plain.Certificate);
        Assert.Equal("My network", plain.AccessText);
        Assert.Equal("Disabled", plain.StatusText);
        Assert.Equal("http://blog.test", plain.Url);
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
    public async Task Required_firewall_runs_the_agents_request()
    {
        var needed = new FirewallSetRequest("2.4.69", [80, 443], true);
        _api.NextResult = ApplyResult.Success() with { RequiredFirewall = needed };

        await AddShopAsync(CertSource.None);

        Assert.Equal(needed, Assert.IsType<FirewallSetRequest>(Assert.Single(_elevator.Requests)));
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

    [Fact]
    public async Task Rows_show_network_addresses()
    {
        _api.Settings = _api.Settings with { NetworkDomain = "arbib.pc", Network = new(true) };
        _api.Sites.Add(Site("shop.test") with { Access = AccessLevel.Network });
        _api.Htdocs.Add(new HtdocsEntry("blog", @"D:\Anvil\htdocs\blog", false, null, AccessLevel.Network));
        _lan = [Lan("Ethernet", "192.168.100.254")];
        var vm = Make();

        await vm.LoadAsync(Ct);

        Assert.Equal(["http://shop.arbib.pc"], vm.Sites.Single().NetworkUrls);
        Assert.Null(vm.Sites.Single().NetworkNote);
        Assert.Equal(["http://192.168.100.254/blog", "http://blog.arbib.pc"], vm.Htdocs.Single().NetworkUrls);
    }

    [Fact]
    public async Task Network_addresses_carry_the_http_port()
    {
        _api.Settings = _api.Settings with { NetworkDomain = "arbib.pc", HttpPort = 8080 };
        _api.Sites.Add(Site("shop.test"));
        _api.Htdocs.Add(new HtdocsEntry("blog", @"D:\Anvil\htdocs\blog", false, null, AccessLevel.Network));
        var vm = Make();

        await vm.LoadAsync(Ct);

        Assert.Equal(["http://shop.arbib.pc:8080"], vm.Sites.Single().NetworkUrls);
        Assert.Equal(["http://192.168.10.2:8080/blog", "http://blog.arbib.pc:8080"], vm.Htdocs.Single().NetworkUrls);
    }

    [Fact]
    public async Task This_pc_only_rows_and_sites_without_a_name_say_so()
    {
        _api.Settings = _api.Settings with { NetworkDomain = "arbib.pc" };
        _api.Sites.Add(Site("my_shop.test"));
        _api.Sites.Add(Site("admin.test") with { Access = AccessLevel.ThisPc });
        _api.Htdocs.Add(new HtdocsEntry("notes", @"D:\Anvil\htdocs\notes", false, null, AccessLevel.ThisPc));
        var vm = Make();

        await vm.LoadAsync(Ct);

        var named = vm.Sites.Single(r => r.Name == "my_shop.test");
        Assert.Empty(named.NetworkUrls);
        Assert.Equal("my_shop.test has no network name: a network name may use only the letters a to z, digits and hyphens.", named.NetworkNote);
        Assert.Empty(vm.Sites.Single(r => r.Name == "admin.test").NetworkUrls);
        Assert.Empty(vm.Htdocs.Single().NetworkUrls);
    }

    [Fact]
    public async Task Copy_puts_an_address_on_the_clipboard()
    {
        var vm = Make();
        await vm.LoadAsync(Ct);

        await vm.CopyUrlCommand.ExecuteAsync("http://shop.arbib.pc");

        Assert.Equal("http://shop.arbib.pc", _clipboard.Text);
    }
}
