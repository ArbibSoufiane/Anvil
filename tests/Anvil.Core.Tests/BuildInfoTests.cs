namespace Anvil.Core.Tests;

public class BuildInfoTests
{
    [Fact]
    public void Version_comes_from_Directory_Build_props() => Assert.Equal("2.1.0", BuildInfo.Version);
}
