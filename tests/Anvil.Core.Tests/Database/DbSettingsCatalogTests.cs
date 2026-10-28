using Anvil.Core.Database;

namespace Anvil.Core.Tests.Database;

public sealed class DbSettingsCatalogTests
{
    // "--no-defaults --verbose --help" output of mariadbd 11.8.9 and mysqld 8.4.13, recorded on 2026-10-27.
    private static string Help(string server) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Database", "Fixtures", $"{server}-verbose-help.txt"));

    private static readonly IReadOnlyDictionary<string, string> MariaDb = DefaultsProbe.Parse(Help("mariadbd"));
    private static readonly IReadOnlyDictionary<string, string> MySql = DefaultsProbe.Parse(Help("mysqld"));

    [Fact]
    public void Parse_reads_max_connections_151()
    {
        Assert.Equal("151", MariaDb["max_connections"]);
        Assert.Equal("151", MySql["max_connections"]);
    }

    [Fact]
    public void Parse_handles_dashes_and_underscores()
    {
        Assert.Equal("134217728", MySql["innodb-buffer-pool-size"]);
        Assert.Equal("134217728", MySql["innodb_buffer_pool_size"]);
        Assert.Equal("utf8mb4", MySql["character_set_server"]);
        Assert.Equal("ONLY_FULL_GROUP_BY,STRICT_TRANS_TABLES,NO_ZERO_IN_DATE,NO_ZERO_DATE,ERROR_FOR_DIVISION_BY_ZERO,NO_ENGINE_SUBSTITUTION",
            MySql["sql_mode"]);
        Assert.Equal("3306", MariaDb["port"]);
    }

    [Fact]
    public void Parse_keeps_options_without_a_default_and_stops_at_the_table_end()
    {
        Assert.Equal("", MySql["init_connect"]);
        Assert.Equal("", MariaDb["bind_address"]);
        Assert.Equal("28800", MySql["wait_timeout"]);
        Assert.DoesNotContain(MySql.Keys, k => k.StartsWith("To ", StringComparison.Ordinal) || k.StartsWith('\''));
        Assert.DoesNotContain(MySql.Keys, k => k.StartsWith("---", StringComparison.Ordinal) || k == "and");
    }

    [Fact]
    public void Every_def_has_description()
    {
        var catalog = new DbSettingsCatalog();

        foreach (var engine in Enum.GetValues<DbEngine>())
        {
            Assert.NotEmpty(catalog.For(engine));
            Assert.All(catalog.For(engine), d =>
            {
                Assert.False(string.IsNullOrWhiteSpace(d.Description), d.Key);
                Assert.Contains(d.Kind, new[] { "int", "size", "bool", "enum", "text" });
            });
        }
    }

    [Fact]
    public void Every_def_is_a_variable_the_server_reports()
    {
        var catalog = new DbSettingsCatalog();

        Assert.All(catalog.For(DbEngine.MariaDb), d => Assert.True(MariaDb.ContainsKey(d.Key), d.Key));
        Assert.All(catalog.For(DbEngine.MySql), d => Assert.True(MySql.ContainsKey(d.Key), d.Key));
    }

    [Fact]
    public void Engine_specific_keys_filtered()
    {
        var catalog = new DbSettingsCatalog();
        var mariadb = catalog.For(DbEngine.MariaDb).Select(d => d.Key).ToList();
        var mysql = catalog.For(DbEngine.MySql).Select(d => d.Key).ToList();

        Assert.Contains("max_statement_time", mariadb);
        Assert.DoesNotContain("max_execution_time", mariadb);
        Assert.Contains("max_execution_time", mysql);
        Assert.DoesNotContain("max_statement_time", mysql);
        Assert.Contains("max_connections", mariadb);
        Assert.Contains("max_connections", mysql);
        Assert.True(catalog.For(DbEngine.MySql).Single(d => d.Key == "lower_case_table_names").InitOnly);
        Assert.Single(catalog.For(DbEngine.MariaDb), d => d.InitOnly);
    }
}
