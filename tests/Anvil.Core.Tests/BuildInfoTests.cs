namespace Anvil.Core.Tests;

public class BuildInfoTests
{
    [Fact]
    public void Version_comes_from_Directory_Build_props() => Assert.Equal("1.0.0", BuildInfo.Version);
}
