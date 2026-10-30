namespace Anvil.Core.Tests;

public class BuildInfoTests
{
    [Fact]
    public void Version_comes_from_Directory_Build_props() => Assert.Equal("2.0.0", BuildInfo.Version);
}
