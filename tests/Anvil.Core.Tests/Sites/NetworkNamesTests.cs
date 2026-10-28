using Anvil.Core.Sites;

namespace Anvil.Core.Tests.Sites;

public sealed class NetworkNamesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static SiteDefinition Site(string name, AccessLevel access) => new()
    {
        Id = name, Kind = SiteKind.Domain, Type = SiteType.Php, Name = name, Root = $@"D:\www\{name}", Access = access, Created = T0,
    };

    private static HtdocsEntry Entry(string folder, AccessLevel access = AccessLevel.Network) =>
        new(folder, $@"D:\Anvil\htdocs\{folder}", false, null, access);

    [Theory]
    [InlineData(" .Arbib.PC. ", "arbib.pc")]
    [InlineData("   ", null)]
    [InlineData("..", null)]
    [InlineData(null, null)]
    public void Domain_is_normalized(string? input, string? expected) => Assert.Equal(expected, NetworkNames.NormalizeDomain(input));

    [Theory]
    [InlineData("arbib.pc", null)]
    [InlineData("team-7.office.lan", null)]
    [InlineData("pc", "A network domain needs at least two parts, such as arbib.pc.")]
    [InlineData("localhost", "A network domain cannot be localhost or end in .localhost.")]
    [InlineData("dev.localhost", "A network domain cannot be localhost or end in .localhost.")]
    [InlineData("arbib_x.pc", "'arbib_x' is not allowed in a network domain: use the letters a to z, digits and hyphens.")]
    [InlineData("a..pc", "'' is not allowed in a network domain: use the letters a to z, digits and hyphens.")]
    [InlineData("-arbib.pc", "'-arbib' is not allowed in a network domain: use the letters a to z, digits and hyphens.")]
    [InlineData("café.pc", "'café' is not allowed in a network domain: use the letters a to z, digits and hyphens.")]
    public void Domain_errors(string domain, string? error) => Assert.Equal(error, NetworkNames.DomainError(domain));

    [Fact]
    public void Domain_longer_than_253_characters_is_refused() =>
        Assert.Equal("A network domain can be at most 253 characters long.",
            NetworkNames.DomainError(string.Join(".", Enumerable.Repeat(new string('a', 63), 4))));

    [Fact]
    public void Network_site_answers_at_its_first_label()
    {
        var plan = NetworkNames.Plan("arbib.pc", [Site("shop.test", AccessLevel.Network)], [], htdocsOnNetwork: true);

        Assert.Equal(new NetworkName("shop", "shop.arbib.pc", "shop.test", null), Assert.Single(plan.Names));
        Assert.Empty(plan.Issues);
    }

    [Fact]
    public void This_pc_and_disabled_sites_get_no_name() =>
        Assert.Empty(NetworkNames.Plan("arbib.pc", [Site("a.test", AccessLevel.ThisPc), Site("b.test", AccessLevel.Network) with { Enabled = false }], [], true).Names);

    [Fact]
    public void Folder_names_are_lower_cased_or_reported()
    {
        var plan = NetworkNames.Plan("arbib.pc", [], [Entry("MyApp"), Entry("my app"), Entry("café"), Entry("old_site")], htdocsOnNetwork: true);

        Assert.Equal([new NetworkName("myapp", "myapp.arbib.pc", null, "MyApp")], plan.Names);
        Assert.Equal("my app has no network name: a network name may use only the letters a to z, digits and hyphens.",
            plan.Issues.Single(i => i.HtdocsFolder == "my app").Reason);
        Assert.Equal(3, plan.Issues.Count);
    }

    [Fact]
    public void Htdocs_off_the_network_gets_no_names() => Assert.Empty(NetworkNames.Plan("arbib.pc", [], [Entry("blog")], htdocsOnNetwork: false).Names);

    [Fact]
    public void A_folder_kept_to_this_pc_gets_no_name() =>
        Assert.Empty(NetworkNames.Plan("arbib.pc", [], [Entry("blog", AccessLevel.ThisPc)], htdocsOnNetwork: true).Names);

    [Fact]
    public void Site_beats_folder_and_older_site_beats_newer()
    {
        var older = Site("shop.test", AccessLevel.Network) with { Created = T0 };
        var newer = Site("shop.local", AccessLevel.Network) with { Created = T0.AddDays(1) };

        var plan = NetworkNames.Plan("arbib.pc", [newer, older], [Entry("shop")], true);

        Assert.Equal("shop.test", Assert.Single(plan.Names).SiteId);
        Assert.Equal("shop.arbib.pc already belongs to shop.test.", plan.Issues.Single(i => i.SiteId == newer.Id).Reason);
        Assert.Equal("shop.arbib.pc already belongs to shop.test.", plan.Issues.Single(i => i.HtdocsFolder == "shop").Reason);
    }

    [Fact]
    public void Folders_differing_only_in_case_report_the_later_one()
    {
        var plan = NetworkNames.Plan("arbib.pc", [], [Entry("blog"), Entry("Blog")], true);

        Assert.Equal("Blog", Assert.Single(plan.Names).HtdocsFolder);
        Assert.Equal(@"blog.arbib.pc already belongs to htdocs\Blog.", Assert.Single(plan.Issues).Reason);
    }

    [Fact]
    public void No_domain_no_names() => Assert.Same(NetworkNamePlan.Empty, NetworkNames.Plan(null, [Site("a.test", AccessLevel.Network)], [], true));
}
