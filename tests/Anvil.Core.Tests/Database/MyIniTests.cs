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

    // Older builds included this file from my.ini for the Development or Production values.
    private string ModeFile(DbEngine e) => Path.Combine(_paths.DbData(e), "mode.cnf");

    [Fact]
    public void EnsureCreated_binds_localhost()
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
        Assert.True(Directory.Exists(Path.Combine(_paths.Logs, "db")));
    }

    [Fact]
    public void New_my_ini_carries_development_values_and_no_include()
    {
        _ini.EnsureCreated(DbEngine.MariaDb, 3306);

        var text = File.ReadAllText(_ini.PathFor(DbEngine.MariaDb));
        Assert.Contains("wait_timeout=28800", text.Replace(" ", ""));
        Assert.Contains("max_statement_time=0", text.Replace(" ", ""));
        Assert.DoesNotContain("!include", text);
        Assert.False(File.Exists(ModeFile(DbEngine.MariaDb)));
    }

    [Fact]
    public void New_mysql_ini_names_the_statement_timeout_in_milliseconds()
    {
        _ini.EnsureCreated(DbEngine.MySql, 3306);

        Assert.Equal("0", _ini.Read(DbEngine.MySql)["max_execution_time"]);
        Assert.False(_ini.Read(DbEngine.MySql).ContainsKey("max_statement_time"));
    }

    [Theory]
    [InlineData("# Written by ArbiPache for Development mode. my.ini includes it last, so these values win.")] // legacy-name
    [InlineData("# Written by Anvil for Development mode. my.ini includes it last, so these values win.")]
    public void An_old_mode_include_is_dropped_and_its_generated_file_deleted_keeping_the_values(string header)
    {
        Directory.CreateDirectory(_paths.DbData(DbEngine.MariaDb));
        File.WriteAllText(_ini.PathFor(DbEngine.MariaDb),
            $"[mysqld]\nport=3306\nwait_timeout = 600\n!include {AppPaths.ForApache(ModeFile(DbEngine.MariaDb))}\n");
        File.WriteAllText(ModeFile(DbEngine.MariaDb), $"{header}\r\n[mysqld]\r\nwait_timeout = 28800\r\n");

        _ini.EnsureCreated(DbEngine.MariaDb, 3306);

        Assert.DoesNotContain("!include", Text(DbEngine.MariaDb));
        Assert.False(File.Exists(ModeFile(DbEngine.MariaDb)));
        Assert.Equal("28800", _ini.Read(DbEngine.MariaDb)["wait_timeout"]);
        Assert.Equal("3306", _ini.Read(DbEngine.MariaDb)["port"]);
    }

    [Fact]
    public void A_mode_file_the_user_wrote_is_kept_though_no_longer_included()
    {
        Directory.CreateDirectory(_paths.DbData(DbEngine.MariaDb));
        File.WriteAllText(_ini.PathFor(DbEngine.MariaDb), $"[mysqld]\nport=3306\n!include {AppPaths.ForApache(ModeFile(DbEngine.MariaDb))}\n");
        File.WriteAllText(ModeFile(DbEngine.MariaDb), "# mine\n[mysqld]\nwait_timeout = 60\n");

        _ini.EnsureCreated(DbEngine.MariaDb, 3306);

        Assert.DoesNotContain("!include", Text(DbEngine.MariaDb));
        Assert.True(File.Exists(ModeFile(DbEngine.MariaDb)));
        Assert.False(_ini.Read(DbEngine.MariaDb).ContainsKey("wait_timeout"));
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

            """);

        _ini.Rebase(DbEngine.MariaDb, @"E:\Old");

        var values = _ini.Read(DbEngine.MariaDb);
        Assert.Equal(AppPaths.ForApache(_ini.DataDir(DbEngine.MariaDb)), values["datadir"]);
        Assert.Equal(AppPaths.ForApache(_ini.ErrorLog), values["log-error"], ignoreCase: true);
        Assert.Equal("F:/db-tmp", values["tmpdir"]);
        Assert.Equal("E:/Older/x", values["innodb_data_home_dir"]);
    }

    [Fact]
    public void Rebase_without_a_file_does_nothing()
    {
        _ini.Rebase(DbEngine.MySql, @"E:\Old");

        Assert.False(File.Exists(_ini.PathFor(DbEngine.MySql)));
    }

    [Fact]
    public void EnsureCreated_keeps_an_existing_file()
    {
        _ini.EnsureCreated(DbEngine.MySql, 3306);
        _ini.Set(DbEngine.MySql, new Dictionary<string, string?> { ["max_connections"] = "300", ["wait_timeout"] = "60" });

        _ini.EnsureCreated(DbEngine.MySql, 3307);

        Assert.Equal("300", _ini.Read(DbEngine.MySql)["max_connections"]);
        Assert.Equal("60", _ini.Read(DbEngine.MySql)["wait_timeout"]);
        Assert.Equal("3306", _ini.Read(DbEngine.MySql)["port"]);
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
    public void New_keys_go_into_mysqld()
    {
        _ini.EnsureCreated(DbEngine.MariaDb, 3306);

        _ini.Set(DbEngine.MariaDb, new Dictionary<string, string?> { ["innodb_buffer_pool_size"] = "512M" });

        var lines = Text(DbEngine.MariaDb).TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.DoesNotContain(lines, l => l.StartsWith("!include ", StringComparison.Ordinal));
        Assert.Contains("innodb_buffer_pool_size = 512M", lines);
        Assert.Equal("512M", _ini.Read(DbEngine.MariaDb)["innodb_buffer_pool_size"]);
    }

    [Fact]
    public void User_comments_preserved()
    {
        Directory.CreateDirectory(_paths.DbData(DbEngine.MySql));
        File.WriteAllText(_ini.PathFor(DbEngine.MySql),
            "# tuned for the shop server\n[mysqld]\nport=3306\n# keep this low: 8 GB box\nmax-connections = 80\n\n[client]\nport=3306\n");

        _ini.Set(DbEngine.MySql, new Dictionary<string, string?> { ["max_connections"] = "120", ["slow_query_log"] = "ON" });

        var text = Text(DbEngine.MySql);
        Assert.Contains("# tuned for the shop server", text);
        Assert.Contains("# keep this low: 8 GB box\nmax-connections = 120\n", text);
        Assert.Equal("120", _ini.Read(DbEngine.MySql)["max_connections"]);
        Assert.Equal("ON", _ini.Read(DbEngine.MySql)["slow_query_log"]);
        // New server keys land in [mysqld], not in the [client] section the user added below it.
        Assert.True(text.IndexOf("slow_query_log", StringComparison.Ordinal) < text.IndexOf("[client]", StringComparison.Ordinal));
        Assert.EndsWith("[client]\nport=3306\n", text);
    }
}
