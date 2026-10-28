using System.Text.Json.Nodes;
using Anvil.Core.Sites;

namespace Anvil.Core.Tests.Sites;

public sealed class LegacySiteJsonTests
{
    private static JsonObject Site(string extra) =>
        JsonNode.Parse($$"""{"id":"x","kind":"Domain","type":"Php","name":"x.test","root":"C:\\x"{{extra}}}""")!.AsObject();

    [Fact]
    public void Ip_site_is_skipped_with_a_warning()
    {
        var s = Site("");
        s["kind"] = "Ip";

        Assert.Equal("x.json: IP sites are no longer supported, so this site was skipped.", LegacySiteJson.Upgrade(s, "x.json"));
    }

    [Theory]
    [InlineData("Everyone", "Network")]
    [InlineData("SpecificIps", "ThisPc")]
    [InlineData("Network", "Network")]
    [InlineData("ThisPc", "ThisPc")]
    public void Access_maps_to_the_two_levels(string old, string now)
    {
        var s = Site($$""","access":"{{old}}","allowedIps":["10.0.0.5"]""");

        Assert.Null(LegacySiteJson.Upgrade(s, "x.json"));
        Assert.Equal(now, (string?)s["access"]);
        Assert.False(s.ContainsKey("allowedIps"));
    }

    [Theory]
    [InlineData("LetsEncrypt")]
    [InlineData("Imported")]
    [InlineData("LoadBalancer")]
    public void Other_certificate_sources_become_none(string old)
    {
        var s = Site($$""","cert":"{{old}}","trustedProxies":["10.0.0.1"],"acceptOnlyFromProxies":true,"passwordProtected":true,"mounts":[]""");

        Assert.Null(LegacySiteJson.Upgrade(s, "x.json"));
        Assert.Equal("None", (string?)s["cert"]);
        Assert.False(s.ContainsKey("trustedProxies") || s.ContainsKey("acceptOnlyFromProxies") || s.ContainsKey("passwordProtected")
                     || s.ContainsKey("mounts"));
    }

    [Fact]
    public void A_current_site_is_left_as_it_is()
    {
        var s = Site("");
        s["cert"] = "LocalDev";
        s["access"] = "ThisPc";
        var before = s.ToJsonString();

        Assert.Null(LegacySiteJson.Upgrade(s, "x.json"));
        Assert.Equal(before, s.ToJsonString());
    }
}
