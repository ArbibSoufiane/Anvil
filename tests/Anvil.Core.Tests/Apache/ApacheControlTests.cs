using Anvil.Core.Apache;

namespace Anvil.Core.Tests.Apache;

public sealed class ApacheControlTests
{
    // The spec: a new Apache version rolls back unless it is healthy within 15 s.
    [Fact]
    public void Apache_gets_15_seconds_to_answer()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), ApacheControl.ServeWait);
    }
}
