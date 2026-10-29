using Anvil.Agent;
using Anvil.Core.Agent;
using Anvil.Core.Components;
using Anvil.Core.Database;
using Anvil.Core.IO;
using Anvil.Core.Paths;
using Anvil.Core.Processes;
using Anvil.Core.Settings;

namespace Anvil.Core.Tests.Agent;

public sealed class DatabaseOperationsTests : IDisposable
{
    private const string Version = "11.8.9";
    private const string Password = "root";
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ap-db-" + Guid.NewGuid().ToString("N")));
    private readonly List<string> _events = [];
    private readonly FakeDb _db;
    private readonly MyIni _ini;

    public DatabaseOperationsTests()
    {
        _db = new FakeDb(_events);
        _ini = new MyIni(_paths);
        _paths.EnsureCreated();
        new SettingsStore(_paths).Save(new AppSettings { StartServicesOnLaunch = false });
        InstallComponent(ComponentKind.MariaDb, Version);
    }

    public void Dispose()
    {
        try { Directory.Delete(_paths.Root, recursive: true); } catch (IOException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Orchestrator> MakeAsync(Func<int, bool>? portFree = null)
    {
        var orchestrator = new Orchestrator(_paths, new SettingsStore(_paths), new FakeTester(), new FakeApache(_events), _ => [],
            new ComponentCatalog([], new ShippedCatalogProvider(_paths)),
            new ComponentInstaller(_paths, new Downloader(new HttpClient()), _ => null), new FakePools(_events), portFree ?? (_ => true), db: _db);
        await orchestrator.InitializeAsync(Ct);
        return orchestrator;
    }

    private void InstallComponent(ComponentKind kind, string version)
    {
        var registry = Path.Combine(_paths.Runtime, "installed.json");
        var all = File.Exists(registry) ? JsonFiles.Read<List<InstalledComponent>>(registry)! : [];
        all.Add(new InstalledComponent(kind, version, Path.Combine(_paths.Runtime, kind.ToString().ToLowerInvariant(), version), new string('a', 64),
            DateTimeOffset.UtcNow));
        JsonFiles.Write(registry, all);
    }

    private async Task<Orchestrator> InstalledAsync()
    {
        var orchestrator = await MakeAsync();
        Assert.True((await orchestrator.InstallDatabaseAsync(DbEngine.MariaDb, Version, Password, Ct)).Ok);
        _events.Clear();
        return orchestrator;
    }

    [Fact]
    public async Task Moved_root_repoints_the_database()
    {
        await InstalledAsync();
        var fwd = AppPaths.ForApache(_paths.Root);
        File.WriteAllText(_ini.PathFor(DbEngine.MariaDb), File.ReadAllText(_ini.PathFor(DbEngine.MariaDb)).Replace(fwd, "E:/Old"));
        File.WriteAllText(Path.Combine(_paths.Data, ".root"), @"E:\Old");

        await MakeAsync();

        var text = File.ReadAllText(_ini.PathFor(DbEngine.MariaDb));
        Assert.DoesNotContain("E:/Old", text);
        Assert.Equal(AppPaths.ForApache(_ini.DataDir(DbEngine.MariaDb)), _ini.Read(DbEngine.MariaDb)["datadir"]);
    }

    [Fact]
    public async Task Install_initializes_saves_and_starts()
    {
        var orchestrator = await MakeAsync();

        var result = await orchestrator.InstallDatabaseAsync(DbEngine.MariaDb, Version, Password, Ct);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(["db init MariaDb 11.8.9", "db start MariaDb 11.8.9"], _events);
        Assert.Equal(DbEngine.MariaDb, orchestrator.Settings.DbEngine);
        Assert.Equal(Version, new SettingsStore(_paths).Load().DbVersion);
        Assert.Equal("127.0.0.1", _ini.Read(DbEngine.MariaDb)["bind-address"]);
        var status = orchestrator.Status().Services.Single(s => s.Id == ServiceId.Database);
        Assert.Equal(ProcessState.Running, status.State);
        Assert.Equal("MariaDB 11.8.9", status.Version);
    }

    [Fact]
    public async Task Install_needs_a_version_that_is_installed_and_a_password()
    {
        var orchestrator = await MakeAsync();

        var missing = await orchestrator.InstallDatabaseAsync(DbEngine.MySql, "8.4.13", Password, Ct);
        var none = await orchestrator.InstallDatabaseAsync(DbEngine.MariaDb, Version, "", Ct);

        Assert.Equal("Install MySQL 8.4.13 first.", missing.Error);
        Assert.Equal("Enter a root password.", none.Error);
        Assert.Empty(_events);
        Assert.Null(orchestrator.Settings.DbEngine);
    }

    [Fact]
    public async Task Install_refuses_a_port_another_program_holds()
    {
        var orchestrator = await MakeAsync(portFree: port => port != 3306);

        var result = await orchestrator.InstallDatabaseAsync(DbEngine.MariaDb, Version, Password, Ct);

        Assert.False(result.Ok);
        Assert.Contains("Port 3306 is already in use", result.Error, StringComparison.Ordinal);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task Existing_database_needs_its_own_root_password()
    {
        _db.Existing.Add(DbEngine.MariaDb);
        _db.AcceptedPassword = "the-old-one";
        var orchestrator = await MakeAsync();

        var wrong = await orchestrator.InstallDatabaseAsync(DbEngine.MariaDb, Version, Password, Ct);
        var right = await orchestrator.InstallDatabaseAsync(DbEngine.MariaDb, Version, "the-old-one", Ct);

        Assert.Contains("is not the root password of the MariaDB database", wrong.Error, StringComparison.Ordinal);
        Assert.True(right.Ok, right.Error);
        Assert.Equal(["db start MariaDb 11.8.9", "db adopt MariaDb", "db stop MariaDb 11.8.9", "db start MariaDb 11.8.9", "db adopt MariaDb"], _events);
        Assert.DoesNotContain(_events, e => e.StartsWith("db init", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Switching_engine_stops_the_other_one()
    {
        var orchestrator = await InstalledAsync();
        InstallComponent(ComponentKind.MySql, "8.4.13");

        var result = await orchestrator.InstallDatabaseAsync(DbEngine.MySql, "8.4.13", Password, Ct);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(["db stop MariaDb 11.8.9", "db init MySql 8.4.13", "db start MySql 8.4.13"], _events);
        Assert.Equal(DbEngine.MySql, orchestrator.Settings.DbEngine);
    }

    [Fact]
    public async Task Settings_rows_show_my_ini_and_probed_defaults()
    {
        var orchestrator = await InstalledAsync();
        _ini.Set(DbEngine.MariaDb, new Dictionary<string, string?>
        {
            ["innodb_buffer_pool_size"] = "128M", ["slow_query_log"] = "OFF", ["max_allowed_packet"] = "64M",
        });

        var rows = (await orchestrator.GetDbSettingsAsync(Ct)).ToDictionary(r => r.Key);

        Assert.Equal(new DbSettingRow("max_connections", null, "151", rows["max_connections"].Description, false, false, false), rows["max_connections"]);
        Assert.Equal("127.0.0.1", rows["bind-address"].Current);
        Assert.Equal("", rows["bind-address"].Default);
        Assert.True(rows["bind-address"].Changed);
        Assert.True(rows["bind-address"].RequiresRestart);
        Assert.False(rows["character-set-server"].Changed); // set in my.ini, but to the default
        Assert.False(rows["innodb_buffer_pool_size"].Changed); // 128M is the default's 134217728 bytes
        Assert.False(rows["slow_query_log"].Changed); // OFF is FALSE
        Assert.True(rows["max_allowed_packet"].Changed);
        Assert.Equal("0", rows["max_statement_time"].Current); // the development value of a new my.ini
        Assert.True(rows["lower_case_table_names"].InitOnly);
        Assert.DoesNotContain("max_execution_time", rows.Keys);
    }

    [Fact]
    public async Task Saving_settings_writes_my_ini_and_restarts()
    {
        var orchestrator = await InstalledAsync();

        var result = await orchestrator.SaveDbSettingsAsync(new Dictionary<string, string?> { ["max_connections"] = "300", ["character-set-server"] = null }, Ct);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("300", _ini.Read(DbEngine.MariaDb)["max_connections"]);
        Assert.False(_ini.Read(DbEngine.MariaDb).ContainsKey("character-set-server"));
        Assert.Equal(["db stop MariaDb 11.8.9", "db start MariaDb 11.8.9"], _events);
    }

    [Fact]
    public async Task A_port_saved_on_the_database_page_is_the_port_Anvil_shows()
    {
        var orchestrator = await InstalledAsync();

        var result = await orchestrator.SaveDbSettingsAsync(new Dictionary<string, string?> { ["port"] = "3308" }, Ct);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(3308, orchestrator.Settings.DbPort);
        Assert.Equal(3308, new SettingsStore(_paths).Load().DbPort);
    }

    [Fact]
    public async Task The_database_port_in_settings_moves_the_running_database()
    {
        var orchestrator = await InstalledAsync();

        var result = await orchestrator.SaveSettingsAsync(orchestrator.Settings with { DbPort = 3308 }, Ct);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("3308", _ini.Read(DbEngine.MariaDb)["port"]);
        Assert.Equal(["db stop MariaDb 11.8.9", "db start MariaDb 11.8.9"], _events);
        Assert.Equal(3308, orchestrator.Settings.DbPort);
    }

    [Fact]
    public async Task A_database_port_another_program_holds_is_refused_in_settings()
    {
        var orchestrator = await MakeAsync(port => port != 3306);

        var free = await orchestrator.SaveSettingsAsync(orchestrator.Settings with { DbPort = 3308 }, Ct);
        var result = await orchestrator.SaveSettingsAsync(orchestrator.Settings with { DbPort = 3306 }, Ct);

        Assert.True(free.Ok, free.Error);
        Assert.Equal("Port 3306 is already in use by another program. Choose another database port. The settings were not saved.", result.Error);
        Assert.Equal(3308, new SettingsStore(_paths).Load().DbPort);
    }

    [Fact]
    public async Task A_server_that_will_not_start_gets_its_old_settings_back()
    {
        var orchestrator = await InstalledAsync();
        var before = File.ReadAllText(_ini.PathFor(DbEngine.MariaDb));
        _db.FailNextStart = "[ERROR] Unknown suffix 'X' used for variable 'innodb_buffer_pool_size'";

        var result = await orchestrator.SaveDbSettingsAsync(new Dictionary<string, string?> { ["innodb_buffer_pool_size"] = "1X" }, Ct);

        Assert.False(result.Ok);
        Assert.Contains("did not start with these settings", result.Error, StringComparison.Ordinal);
        Assert.Contains("Unknown suffix", result.Error, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(_ini.PathFor(DbEngine.MariaDb)));
        Assert.Equal(["db stop MariaDb 11.8.9", "db start MariaDb 11.8.9", "db start MariaDb 11.8.9"], _events);
        Assert.Equal(ProcessState.Running, orchestrator.Status().Services.Single(s => s.Id == ServiceId.Database).State);
    }

    [Fact]
    public async Task New_my_ini_carries_the_development_values_and_no_mode_file()
    {
        var orchestrator = await InstalledAsync();

        var rows = (await orchestrator.GetDbSettingsAsync(Ct)).ToDictionary(r => r.Key);
        Assert.Equal("28800", rows["wait_timeout"].Current);
        Assert.Equal("ON", _ini.Read(DbEngine.MariaDb)["general_log"]);
        Assert.False(File.Exists(Path.Combine(_paths.DbData(DbEngine.MariaDb), "mode.cnf")));
    }

    [Fact]
    public async Task Init_only_and_unknown_keys_are_refused()
    {
        var orchestrator = await InstalledAsync();

        var init = await orchestrator.SaveDbSettingsAsync(new Dictionary<string, string?> { ["lower_case_table_names"] = "2" }, Ct);
        var unknown = await orchestrator.SaveDbSettingsAsync(new Dictionary<string, string?> { ["skip-grant-tables"] = "1" }, Ct);

        Assert.Equal("lower_case_table_names is fixed when the database is created.", init.Error);
        Assert.Equal("skip-grant-tables is not a setting Anvil manages. Edit my.ini for it.", unknown.Error);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task Database_starts_and_stops_with_every_service()
    {
        var orchestrator = await InstalledAsync();

        await orchestrator.StopAsync(null, Ct);
        await orchestrator.StartAsync(ServiceId.Database, Ct);

        Assert.Contains("db stop MariaDb 11.8.9", _events);
        Assert.Equal("db start MariaDb 11.8.9", _events[^1]);
    }

    [Fact]
    public async Task Password_and_backup_need_a_running_server()
    {
        var orchestrator = await InstalledAsync();

        Assert.True((await orchestrator.SetDbRootPasswordAsync("root", Ct)).Ok);
        Assert.EndsWith(".sql.gz", await orchestrator.BackupDatabaseAsync(Ct), StringComparison.Ordinal);
        await orchestrator.StopAsync(ServiceId.Database, Ct);

        Assert.Equal("Start the database first.", (await orchestrator.SetDbRootPasswordAsync("third-pass", Ct)).Error);
        Assert.Equal("Start the database first.", (await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.BackupDatabaseAsync(Ct))).Message);
        Assert.Equal("Enter a root password.", (await orchestrator.SetDbRootPasswordAsync("", Ct)).Error);
    }

    [Fact]
    public async Task Database_minor_update_restarts_on_the_new_version()
    {
        var orchestrator = await InstalledAsync();
        InstallComponent(ComponentKind.MariaDb, "11.8.10");

        var result = await orchestrator.ActivateAsync(ComponentKind.MariaDb, "11.8.10", Ct);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(["db stop MariaDb 11.8.9", "db start MariaDb 11.8.10"], _events);
        Assert.Equal("11.8.10", new SettingsStore(_paths).Load().DbVersion);
    }

    [Fact]
    public async Task Database_update_that_does_not_start_switches_back()
    {
        var orchestrator = await InstalledAsync();
        InstallComponent(ComponentKind.MariaDb, "11.8.10");
        _db.FailNextStart = "InnoDB: unsupported redo log format.";

        var result = await orchestrator.ActivateAsync(ComponentKind.MariaDb, "11.8.10", Ct);

        Assert.False(result.Ok);
        Assert.Equal("MariaDB 11.8.10 did not start: InnoDB: unsupported redo log format. MariaDB 11.8.9 is running again.", result.Error);
        Assert.Equal(["db stop MariaDb 11.8.9", "db start MariaDb 11.8.10", "db start MariaDb 11.8.9"], _events);
        Assert.Equal(Version, new SettingsStore(_paths).Load().DbVersion);
        Assert.Equal(ProcessState.Running, orchestrator.Status().Services.Single(s => s.Id == ServiceId.Database).State);
    }

    [Fact]
    public async Task Database_series_upgrades_are_not_updates()
    {
        var orchestrator = await InstalledAsync();
        InstallComponent(ComponentKind.MariaDb, "12.0.1");

        var result = await orchestrator.ActivateAsync(ComponentKind.MariaDb, "12.0.1", Ct);

        Assert.Equal("MariaDB 12.0.1 is a new series, not an update of 11.8.9. Back up your databases first: a new series upgrades the data files and cannot be undone.", result.Error);
        Assert.Empty(_events);
        Assert.Equal("MySQL is not the active database.", (await orchestrator.ActivateAsync(ComponentKind.MySql, "8.4.13", Ct)).Error);
    }
}
