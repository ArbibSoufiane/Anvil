using Anvil.Core.Components;
using Anvil.Core.Database;
using Anvil.Integration.Tests.Support;

namespace Anvil.Integration.Tests;

// The Database page's defaults come from the installed server itself, never from numbers typed into Anvil.
public sealed class DefaultsProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MariaDb_reports_its_own_defaults()
    {
        var paths = Runtimes.NewRoot("probe-mariadb");
        var installed = await Runtimes.InstallAsync(paths, await Runtimes.LatestMariaDbAsync("11.8", Ct), Ct);

        var defaults = await DefaultsProbe.ReadAsync(Path.Combine(installed.Path, "bin", "mariadbd.exe"), Ct);

        Assert.Equal("151", defaults["max_connections"]);
        Assert.Equal("3306", defaults["port"]);
        Assert.All(new DbSettingsCatalog().For(DbEngine.MariaDb), d => Assert.True(defaults.ContainsKey(d.Key), d.Key));
    }

    [Fact]
    public async Task MySql_reports_its_own_defaults()
    {
        var paths = Runtimes.NewRoot("probe-mysql");
        var installed = await Runtimes.InstallAsync(paths, Runtimes.Pinned(ComponentKind.MySql), Ct);

        var defaults = await DefaultsProbe.ReadAsync(Path.Combine(installed.Path, "bin", "mysqld.exe"), Ct);

        Assert.Equal("151", defaults["max_connections"]);
        Assert.Equal("utf8mb4", defaults["character_set_server"]);
        Assert.All(new DbSettingsCatalog().For(DbEngine.MySql), d => Assert.True(defaults.ContainsKey(d.Key), d.Key));
    }
}
