using Anvil.Core.Elevation;
using Anvil.Core.Paths;
using Anvil.Core.Settings;
using Anvil.Core.Sites;

namespace Anvil.Core.Tests.Elevation;

public sealed class FirewallRulePlannerTests
{
    private static readonly AppPaths P = new(@"C:\Anvil");

    private static SiteDefinition Site(AccessLevel access, bool enabled = true) => new()
    {
        Id = "shop.test", Kind = SiteKind.Domain, Type = SiteType.Static, Name = "shop.test", Root = @"D:\www\shop", Access = access, Enabled = enabled,
    };

    [Fact]
    public void Plans_web_server_and_name_service_rules() => Assert.Equal(
    [
        new FirewallRule("Anvil web server", @"C:\Anvil\runtime\apache\2.4.69\bin\httpd.exe", FirewallProtocol.Tcp, "80,443", 3, "LocalSubnet"),
        new FirewallRule("Anvil name service", @"C:\Anvil\Anvil.Agent.exe", FirewallProtocol.Udp, "53", 3, "LocalSubnet"),
    ], FirewallRulePlanner.Plan(new FirewallSetRequest("2.4.69", [80, 443, 80], NameService: true), P));

    [Fact]
    public void Without_apache_there_is_no_web_rule() =>
        Assert.Equal(["Anvil name service"], FirewallRulePlanner.Plan(new FirewallSetRequest(null, [80], true), P).Select(r => r.Name));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Port_out_of_range_rejected(int port) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => FirewallRulePlanner.Plan(new FirewallSetRequest("2.4.69", [80, port], false), P));

    [Fact]
    public void Nothing_open_needs_nothing() =>
        Assert.Null(FirewallRulePlanner.Needed(new AppSettings { ActiveApache = "2.4.69", Network = new(false), NetworkDomain = "arbib.pc" }, [Site(AccessLevel.ThisPc)]));

    [Fact]
    public void A_turned_off_network_site_needs_nothing() =>
        Assert.Null(FirewallRulePlanner.Needed(new AppSettings { ActiveApache = "2.4.69", Network = new(false) }, [Site(AccessLevel.Network, enabled: false)]));

    [Fact]
    public void Network_site_needs_the_web_ports_and_the_name_service_when_a_domain_is_set() =>
        Assert.Equal(new FirewallSetRequest("2.4.69", [80, 443], true),
            FirewallRulePlanner.Needed(new AppSettings { ActiveApache = "2.4.69", Network = new(false), NetworkDomain = "arbib.pc" }, [Site(AccessLevel.Network)]));

    [Fact]
    public void Htdocs_on_the_network_needs_the_web_ports() =>
        Assert.Equal(new FirewallSetRequest("2.4.69", [8080, 8443], false),
            FirewallRulePlanner.Needed(new AppSettings { ActiveApache = "2.4.69", HttpPort = 8080, HttpsPort = 8443 }, []));

    [Fact]
    public void Requests_compare_ports_by_value()
    {
        Assert.Equal(new FirewallSetRequest("2.4.69", [80, 443], false), new FirewallSetRequest("2.4.69", new List<int> { 80, 443 }, false));
        Assert.Equal(new FirewallSetRequest("2.4.69", [80, 443], false).GetHashCode(), new FirewallSetRequest("2.4.69", new List<int> { 80, 443 }, false).GetHashCode());
        Assert.NotEqual(new FirewallSetRequest("2.4.69", [80, 443], false), new FirewallSetRequest("2.4.69", [80, 8443], false));
        Assert.NotEqual(new FirewallSetRequest("2.4.69", [80, 443], false), new FirewallSetRequest("2.4.69", [80, 443], true));
    }

    [Theory]
    [InlineData(@"C:\Anvil\runtime\apache\2.4.68\bin\httpd.exe", "Apache HTTP Server", true)]
    [InlineData(@"c:\anvil\RUNTIME\apache\2.4.69\bin\HTTPD.EXE", "x", true)]
    [InlineData(@"C:\Anvil\Anvil.Agent.exe", "x", true)]
    [InlineData(null, "Anvil web server", true)]
    [InlineData(null, "Anvil HTTP 80", true)]
    [InlineData(@"C:\Other\httpd.exe", "Apache HTTP Server", false)]
    [InlineData(@"C:\Anvil\runtime\php\8.5.12\php-cgi.exe", "PHP", false)]
    [InlineData(@"%ProgramFiles%\httpd.exe", "Apache HTTP Server", false)]
    [InlineData(null, "Anvilish", false)]
    public void Recognises_rules_for_its_own_programs(string? program, string name, bool ours) =>
        Assert.Equal(ours, FirewallRulePlanner.IsOurs(new ExistingFirewallRule(name, program, 6, "*", 3, "*", true, true), P));

    [Fact]
    public void Block_or_public_rule_for_its_program_is_not_satisfied()
    {
        var planned = FirewallRulePlanner.Plan(new FirewallSetRequest("2.4.69", [80, 443], false), P);
        var exact = new ExistingFirewallRule("Anvil web server", planned[0].Program, 6, "80,443", 3, "LocalSubnet", true, true);

        Assert.True(FirewallRulePlanner.Satisfied(planned, [exact], P));
        Assert.False(FirewallRulePlanner.Satisfied(planned, [], P));
        Assert.False(FirewallRulePlanner.Satisfied(planned, [exact with { Enabled = false }], P));
        Assert.False(FirewallRulePlanner.Satisfied(planned, [exact with { LocalPorts = "80" }], P));
        Assert.False(FirewallRulePlanner.Satisfied(planned, [exact, exact with { Name = "Apache HTTP Server", Allow = false }], P));
        Assert.False(FirewallRulePlanner.Satisfied(planned, [exact, exact with { Name = "Apache HTTP Server", Profiles = 4 }], P));
        Assert.True(FirewallRulePlanner.Satisfied(planned, [exact, exact with { Name = "Apache HTTP Server", Allow = false, Enabled = false }], P));
        Assert.True(FirewallRulePlanner.Satisfied(planned,
            [exact with { Name = "ANVIL WEB SERVER", Program = planned[0].Program.ToUpperInvariant(), RemoteAddresses = "localsubnet" }], P));
    }
}
