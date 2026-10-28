using Anvil.Core.Apache;
using Anvil.Core.Sites;

namespace Anvil.Core.Tests.Apache;

public sealed class AccessRulesTests
{
    private static SiteDefinition Site(AccessLevel access) => new()
    {
        Id = "shop.example.com",
        Kind = SiteKind.Domain,
        Type = SiteType.Php,
        Name = "shop.example.com",
        Root = @"D:\Sites\shop\public",
        Access = access,
    };

    [Fact]
    public void Network_is_private_ranges() =>
        Assert.Equal("Require ip 127.0.0.0/8 ::1 10.0.0.0/8 172.16.0.0/12 192.168.0.0/16 fc00::/7 fe80::/10",
            AccessRules.Render(Site(AccessLevel.Network)));

    [Fact]
    public void This_pc_is_local() => Assert.Equal("Require local", AccessRules.Render(Site(AccessLevel.ThisPc)));
}
