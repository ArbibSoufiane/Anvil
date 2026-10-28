using Anvil.Core.Database;
using Anvil.Core.Paths;
using Anvil.Core.Processes;

namespace Anvil.Core.Tests.Database;

public sealed class DatabaseManagerTests : IAsyncDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), $"dbmanager {Guid.NewGuid():N}"));
    private readonly JobObject _job = new();
    private readonly ProcessSupervisor _supervisor;
    private readonly MyIni _ini;
    private readonly DatabaseManager _manager;

    public DatabaseManagerTests()
    {
        _paths.EnsureCreated();
        _supervisor = new ProcessSupervisor(_job, TimeProvider.System);
        _ini = new MyIni(_paths);
        _manager = new DatabaseManager(_paths, _supervisor, new DbSecrets(_paths), _ini);
    }

    public async ValueTask DisposeAsync()
    {
        await _supervisor.DisposeAsync();
        _job.Dispose();
        Directory.Delete(_paths.Root, recursive: true);
    }

    [Fact]
    public async Task Initialize_refuses_a_datadir_that_already_holds_a_database()
    {
        _ini.EnsureCreated(DbEngine.MariaDb, 3306);
        Directory.CreateDirectory(_ini.DataDir(DbEngine.MariaDb));
        File.WriteAllText(Path.Combine(_ini.DataDir(DbEngine.MariaDb), "ibdata1"), "");

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _manager.InitializeAsync(DbEngine.MariaDb, "11.8.9", "pw", TestContext.Current.CancellationToken));

        Assert.Contains("already holds a database", e.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_ini.DataDir(DbEngine.MariaDb), "ibdata1")));
    }

    [Fact]
    public void Spec_reads_only_the_sites_my_ini()
    {
        _ini.EnsureCreated(DbEngine.MySql, 3307);

        var spec = _manager.Spec(DbEngine.MySql, "8.4.13");

        Assert.Equal("mysql", spec.Name);
        Assert.Equal(Path.Combine(_paths.DbRuntime(DbEngine.MySql, "8.4.13"), "bin", "mysqld.exe"), spec.Exe);
        Assert.Equal([$"--defaults-file={_ini.PathFor(DbEngine.MySql)}"], spec.Args);
        Assert.Equal(3307, _manager.Port(DbEngine.MySql));
    }
}
