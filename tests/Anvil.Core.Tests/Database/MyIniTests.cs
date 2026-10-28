using Anvil.Core.Database;
using Anvil.Core.Paths;

namespace Anvil.Core.Tests.Database;

public sealed class MyIniTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), $"myini {Guid.NewGuid():N}"));
    private readonly MyIni _ini;

    public MyIniTests()
    {
        _paths.EnsureCreated();
        _ini = new MyIni(_paths);
    }

    public void Dispose() => Directory.Delete(_paths.Root, recursive: true);

    private string Text(DbEngine e) => File.ReadAllText(_ini.PathFor(e));

    [Fact]
    public void EnsureCreated_binds_localhost_and_includes_mode_file()
    {
        _ini.EnsureCreated(DbEngine.MariaDb, 3306);

        var values = _ini.Read(DbEngine.MariaDb);
        Assert.Equal(Path.Combine(_paths.DbData(DbEngine.MariaDb), "my.ini"), _ini.PathFor(DbEngine.MariaDb));
        Assert.Equal("127.0.0.1", values["bind-address"]);
        Assert.Equal("3306", values["port"]);
        Assert.Equal("utf8mb4", values["character-set-server"]);
        // Option files treat backslashes as escapes, so paths use forward slashes (quoted: the root may contain spaces).
        Assert.Equal(AppPaths.ForApache(_ini.DataDir(DbEngine.MariaDb)), values["datadir"]);
        Assert.Equal(AppPaths.ForApache(Path.Combine(_paths.Logs, "db", "error.log")), values["log-error"]);
        Assert.Contains($"datadir=\"{AppPaths.ForApache(_ini.DataDir(DbEngine.MariaDb))}\"", Text(DbEngine.MariaDb));

        var lines = Text(DbEngine.MariaDb).TrimEnd().Split('\n');
        Assert.Equal($"!include {AppPaths.ForApache(_ini.ModeFile(DbEngine.MariaDb))}", lines[^1].TrimEnd('\r'));
        Assert.Equal("[mysqld]", File.ReadAllText(_ini.ModeFile(DbEngine.MariaDb)).Trim());
        Assert.True(Directory.Exists(Path.Combine(_paths.Logs, "db")));
    }

    [Fact]
    public void Rebase_repoints_paths_inside_the_old_root()
    {
        _ini.EnsureCreated(DbEngine.MariaDb, 3306);
        var fwd = AppPaths.ForApache(_paths.Root);
        string Old(string path) => "E:/Old" + AppPaths.ForApache(path)[fwd.Length..];
        // A folder copied here from E:\Old: my.ini still names the old place, in the user's spelling.
        File.WriteAllText(_ini.PathFor(DbEngine.MariaDb), $"""
            [mysqld]
            datadir="{Old(_ini.DataDir(DbEngine.MariaDb))}"
            log-error="{Old(_ini.ErrorLog).ToLowerInvariant()}"
            tmpdir="F:/db-tmp"
            innodb_data_home_dir="E:/Older/x"
            !include {Old(_ini.ModeFile(DbEngine.MariaDb))}

            """);

        _ini.Rebase(DbEngine.MariaDb, @"E:\Old");

        var values = _ini.Read(DbEngine.MariaDb);
        Assert.Equal(AppPaths.ForApache(_ini.DataDir(DbEngine.MariaDb)), values["datadir"]);
        Assert.Equal(AppPaths.ForApache(_ini.ErrorLog), values["log-error"], ignoreCase: true);
        Assert.Equal("F:/db-tmp", values["tmpdir"]);
        Assert.Equal("E:/Older/x", values["innodb_data_home_dir"]);
        Assert.Equal($"!include {AppPaths.ForApache(_ini.ModeFile(DbEngine.MariaDb))}", Text(DbEngine.MariaDb).TrimEnd().Split('\n')[^1].TrimEnd('\r'));

        _ini.Set(DbEngine.MariaDb, new Dictionary<string, string?> { ["max_connections"] = "300" });
        Assert.Single(Text(DbEngine.MariaDb).Split('\n'), l => l.StartsWith("!include", StringComparison.Ordinal));
    }

    [Fact]
    public void Rebase_without_a_file_does_nothing()
    {
        _ini.Rebase(DbEngine.MySql, @"E:\Old");

        Assert.False(File.Exists(_ini.PathFor(DbEngine.MySql)));
    }

    [Fact]
    public void EnsureCreated_keeps_an_existing_file_and_mode_file()
    {
        _ini.EnsureCreated(DbEngine.MySql, 3306);
        _ini.Set(DbEngine.MySql, new Dictionary<string, string?> { ["max_connections"] = "300" });
        File.WriteAllText(_ini.ModeFile(DbEngine.MySql), "[mysqld]\nmax_execution_time = 30000\n");

        _ini.EnsureCreated(DbEngine.MySql, 3307);

        Assert.Equal("300", _ini.Read(DbEngine.MySql)["max_connections"]);
        Assert.Equal("3306", _ini.Read(DbEngine.MySql)["port"]);
        Assert.Contains("max_execution_time", File.ReadAllText(_ini.ModeFile(DbEngine.MySql)));
    }

    [Fact]
    public void Set_null_removes_key()
    {
        _ini.EnsureCreated(DbEngine.MariaDb, 3306);
        _ini.Set(DbEngine.MariaDb, new Dictionary<string, string?> { ["max_connections"] = "300", ["wait_timeout"] = "600" });

        _ini.Set(DbEngine.MariaDb, new Dictionary<string, string?> { ["max_connections"] = null });

        var values = _ini.Read(DbEngine.MariaDb);
        Assert.False(values.ContainsKey("max_connections"));
        Assert.Equal("600", values["wait_timeout"]);
        Assert.DoesNotContain("max_connections", Text(DbEngine.MariaDb));
    }

    [Fact]
    public void New_keys_go_above_the_mode_include()
    {
        _ini.EnsureCreated(DbEngine.MariaDb, 3306);

        _ini.Set(DbEngine.MariaDb, new Dictionary<string, string?> { ["innodb_buffer_pool_size"] = "512M" });

        var lines = Text(DbEngine.MariaDb).TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.StartsWith("!include ", lines[^1], StringComparison.Ordinal);
        Assert.Single(lines, l => l.StartsWith("!include ", StringComparison.Ordinal));
        Assert.Contains("innodb_buffer_pool_size = 512M", lines);
    }

    [Fact]
    public void User_comments_preserved()
    {
        Directory.CreateDirectory(_paths.DbData(DbEngine.MySql));
        File.WriteAllText(_ini.PathFor(DbEngine.MySql),
            "# tuned for the shop server\n[mysqld]\nport=3306\n# keep this low: 8 GB box\nmax-connections = 80\n\n[client]\nport=3306\n" +
            $"!include {AppPaths.ForApache(_ini.ModeFile(DbEngine.MySql))}\n");

        _ini.Set(DbEngine.MySql, new Dictionary<string, string?> { ["max_connections"] = "120", ["slow_query_log"] = "ON" });

        var text = Text(DbEngine.MySql);
        Assert.Contains("# tuned for the shop server", text);
        Assert.Contains("# keep this low: 8 GB box\nmax-connections = 120\n", text);
        Assert.Equal("120", _ini.Read(DbEngine.MySql)["max_connections"]);
        Assert.Equal("ON", _ini.Read(DbEngine.MySql)["slow_query_log"]);
        // New server keys land in [mysqld], not in the [client] section the user added below it.
        Assert.True(text.IndexOf("slow_query_log", StringComparison.Ordinal) < text.IndexOf("[client]", StringComparison.Ordinal));
        Assert.EndsWith($"[client]\nport=3306\n!include {AppPaths.ForApache(_ini.ModeFile(DbEngine.MySql))}\n", text);
    }
}
