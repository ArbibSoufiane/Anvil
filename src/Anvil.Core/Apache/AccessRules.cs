using Anvil.Core.Sites;

namespace Anvil.Core.Apache;

/// <summary>The Require line for a site's &lt;Directory&gt;: this PC only, or the private address ranges as well.</summary>
public static class AccessRules
{
    public static string Render(SiteDefinition s) => s.Access switch
    {
        AccessLevel.ThisPc => "Require local",
        AccessLevel.Network => $"Require ip {ConfigRenderer.PrivateRanges}",
        _ => throw new ArgumentOutOfRangeException(nameof(s), s.Access, null),
    };
}
