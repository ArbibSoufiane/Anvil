using Anvil.Core.Elevation;

namespace Anvil.Integration.Tests;

// Reads this PC's real Windows Firewall without administrator rights; nothing is changed.
public sealed class FirewallReaderTests
{
    [Fact]
    public void Inbound_rules_can_be_read_without_administrator_rights()
    {
        var rules = WindowsFirewallReader.Read();

        Assert.NotEmpty(rules);
        Assert.All(rules, r => Assert.False(string.IsNullOrEmpty(r.Name)));
        Assert.Contains(rules, r => r.Program is not null && r.Enabled);
    }
}
