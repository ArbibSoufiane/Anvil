using Anvil.Core.Tests.Support;

namespace Anvil.Core.Tests;

public class ManifestTests
{
    [Fact]
    public void App_manifest_is_asInvoker() =>
        Assert.Contains("level=\"asInvoker\"", File.ReadAllText(Repo.Path("src/Anvil.App/app.manifest")));

    [Fact]
    public void Elevator_manifest_requires_admin() =>
        Assert.Contains("level=\"requireAdministrator\"", File.ReadAllText(Repo.Path("src/Anvil.Elevator/app.manifest")));
}
