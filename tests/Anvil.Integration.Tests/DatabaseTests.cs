using Anvil.Core.Components;
using Anvil.Core.Database;
using Anvil.Core.Paths;
using Anvil.Core.Processes;
using Anvil.Integration.Tests.Support;
using MySqlConnector;

namespace Anvil.Integration.Tests;

// Real MariaDB 11.8 and MySQL 8.4 servers, initialized in throwaway roots on ports that stay clear of a local 3306.
public sealed class DatabaseTests : IAsyncDisposable
{
    private const string Password = "Init \"pw\" \\ 1";
    private readonly JobObject _job = new();
    private readonly ProcessSupervisor _supervisor;

    public DatabaseTests() => _supervisor = new ProcessSupervisor(_job, TimeProvider.System);

    public async ValueTask DisposeAsync()
    {
        await _supervisor.DisposeAsync();
        _job.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Server(AppPaths Paths, MyIni Ini, DbSecrets Secrets, DatabaseManager Manager, DbEngine Engine, string Version, int Port);

    private async Task<Server> InstallAsync(DbEngine engine, int port)
    {
        var paths = Runtimes.NewRoot($"db-{engine}");
        var release = engine == DbEngine.MariaDb ? await Runtimes.LatestMariaDbAsync("11.8", Ct) : Runtimes.Pinned(ComponentKind.MySql);
        var installed = await Runtimes.InstallAsync(paths, release, Ct);
        var ini = new MyIni(paths);
        var secrets = new DbSecrets(paths);
        ini.EnsureCreated(engine, port);
        return new Server(paths, ini, secrets, new DatabaseManager(paths, _supervisor, secrets, ini), engine, installed.Version, port);
    }

    private static async Task<string> ScalarAsync(Server s, string password, string sql)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = "127.0.0.1", Port = (uint)s.Port, UserID = "root", Password = password, Pooling = false, AllowPublicKeyRetrieval = true,
        };
        await using var connection = new MySqlConnection(builder.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new MySqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task StartAsync(Server s)
    {
        _supervisor.Start(s.Manager.Spec(s.Engine, s.Version));
        await s.Manager.WaitUntilReadyAsync(s.Engine, Ct);
    }

    [Fact]
    public async Task MariaDb_initializes_starts_and_answers_version()
    {
        var s = await InstallAsync(DbEngine.MariaDb, 13306);
        await s.Manager.InitializeAsync(s.Engine, s.Version, Password, Ct);
        await StartAsync(s);

        Assert.StartsWith("11.8.", await ScalarAsync(s, Password, "SELECT VERSION()"), StringComparison.Ordinal);
        var runtime = await s.Manager.RuntimeValuesAsync(s.Engine, Ct);
        Assert.Equal("127.0.0.1", runtime["bind_address"]);
        Assert.Equal("0.000000", runtime["max_statement_time"]);
        Assert.Equal("28800", runtime["wait_timeout"]);
        Assert.Equal("2.000000", runtime["long_query_time"]);
        Assert.Equal("ON", runtime["general_log"]);
        Assert.Equal("utf8mb4", runtime["character-set-server"]);
        Assert.Equal("0", await ScalarAsync(s, Password, "SELECT COUNT(*) FROM mysql.user WHERE User = 'root' AND Host = '%'"));

        await s.Manager.SetRootPasswordAsync(s.Engine, "changed-later", Ct);
        Assert.Equal("1", await ScalarAsync(s, "changed-later", "SELECT 1"));
        await Assert.ThrowsAsync<MySqlException>(() => ScalarAsync(s, Password, "SELECT 1"));
        Assert.Equal("changed-later", s.Secrets.LoadRootPassword());

        await s.Manager.StopAsync(s.Engine, s.Version, Ct);
    }

    [Fact]
    public async Task MySql_initializes_starts_and_answers_version()
    {
        var s = await InstallAsync(DbEngine.MySql, 13307);
        await s.Manager.InitializeAsync(s.Engine, s.Version, Password, Ct);
        await StartAsync(s);

        Assert.StartsWith("8.4.", await ScalarAsync(s, Password, "SELECT VERSION()"), StringComparison.Ordinal);
        var runtime = await s.Manager.RuntimeValuesAsync(s.Engine, Ct);
        Assert.Equal("127.0.0.1", runtime["bind_address"]);
        Assert.Equal("0", runtime["max_execution_time"]);
        Assert.Equal("28800", runtime["wait_timeout"]);
        Assert.Equal("ON", runtime["general_log"]);

        await s.Manager.StopAsync(s.Engine, s.Version, Ct);
    }

    [Fact]
    public async Task Backup_dumps_every_database()
    {
        var s = await InstallAsync(DbEngine.MariaDb, 13309);
        await s.Manager.InitializeAsync(s.Engine, s.Version, Password, Ct);
        await StartAsync(s);
        await ScalarAsync(s, Password, "CREATE DATABASE shop; CREATE TABLE shop.orders (id INT PRIMARY KEY, total DECIMAL(9,2)); " +
            "INSERT INTO shop.orders VALUES (7, 19.90); SELECT 1");

        var file = await new DbBackup(s.Paths, s.Secrets, TimeProvider.System).BackupAllAsync(s.Engine, s.Version, Ct);
        await s.Manager.StopAsync(s.Engine, s.Version, Ct);

        Assert.Matches(@"\\db\\\d{4}-\d{2}-\d{2}-\d{6}\.sql\.gz$", file);
        await using var gzip = new System.IO.Compression.GZipStream(File.OpenRead(file), System.IO.Compression.CompressionMode.Decompress);
        var sql = await new StreamReader(gzip).ReadToEndAsync(Ct);
        Assert.Contains("CREATE DATABASE /*!32312 IF NOT EXISTS*/ `shop`", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO `orders` VALUES", sql, StringComparison.Ordinal);
        Assert.Contains("(7,19.90)", sql, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(s.Paths.Tmp, "db-client-*"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(file)!, "*.partial"));
    }

    [Fact]
    public async Task Server_that_will_not_start_says_why_and_known_passwords_are_adopted()
    {
        var s = await InstallAsync(DbEngine.MariaDb, 13310);
        await s.Manager.InitializeAsync(s.Engine, s.Version, Password, Ct);
        var server = new DatabaseServer(s.Paths, _supervisor, TimeProvider.System);
        s.Ini.Set(s.Engine, new Dictionary<string, string?> { ["innodb_buffer_pool_size"] = "banana" });

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync(s.Engine, s.Version, Ct));

        Assert.Contains("Unknown suffix 'b' used for variable 'innodb-buffer-pool-size' (value 'banana')", e.Message, StringComparison.Ordinal);
        Assert.NotEqual(ProcessState.Running, server.Status(s.Engine)?.State);

        s.Ini.Set(s.Engine, new Dictionary<string, string?> { ["innodb_buffer_pool_size"] = null });
        await server.StartAsync(s.Engine, s.Version, Ct);
        Assert.False(await server.AdoptRootPasswordAsync(s.Engine, "not-the-password", Ct));
        Assert.True(await server.AdoptRootPasswordAsync(s.Engine, Password, Ct));
        Assert.Equal("151", (await server.DefaultsAsync(s.Engine, s.Version, Ct))["max_connections"]);
        await server.StopAsync(s.Engine, s.Version, Ct);
    }

    [Fact]
    public async Task Stop_is_clean()
    {
        var s = await InstallAsync(DbEngine.MariaDb, 13308);
        await s.Manager.InitializeAsync(s.Engine, s.Version, Password, Ct);
        await StartAsync(s);
        var started = DateTime.UtcNow;

        await s.Manager.StopAsync(s.Engine, s.Version, Ct);

        // A clean shutdown finishes well inside the grace period, so the supervisor never had to kill the server.
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30));
        Assert.Equal(ProcessState.Stopped, _supervisor.Snapshot().Single(p => p.Name == "mariadb").State);
        Assert.Contains("Shutdown complete", await File.ReadAllTextAsync(s.Ini.ErrorLog, Ct), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(s.Paths.Tmp, "db-client-*"));
    }
}
