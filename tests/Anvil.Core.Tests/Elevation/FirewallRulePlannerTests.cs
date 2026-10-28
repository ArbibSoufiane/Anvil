using Anvil.Core.Elevation;

namespace Anvil.Core.Tests.Elevation;

public sealed class FirewallRulePlannerTests
{
    [Fact]
    public void LocalNetwork_is_private_domain_local_subnet() =>
        Assert.Equal(
        [
            new FirewallRule("Anvil HTTP 80", 80, 3, "LocalSubnet"),
            new FirewallRule("Anvil HTTP 443", 443, 3, "LocalSubnet"),
        ], FirewallRulePlanner.Plan(new FirewallSetRequest([80, 443, 80], FirewallScope.LocalNetwork)));

    [Fact]
    public void Internet_is_all_profiles_any_address() =>
        Assert.Equal([new FirewallRule("Anvil HTTP 8080", 8080, 0x7FFFFFFF, "*")],
            FirewallRulePlanner.Plan(new FirewallSetRequest([8080], FirewallScope.Internet)));

    [Fact]
    public void No_ports_plans_no_rules() => Assert.Empty(FirewallRulePlanner.Plan(new FirewallSetRequest([], FirewallScope.Internet)));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Port_out_of_range_rejected(int port) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => FirewallRulePlanner.Plan(new FirewallSetRequest([80, port], FirewallScope.LocalNetwork)));
}
