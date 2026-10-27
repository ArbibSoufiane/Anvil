using ArbiPache.Agent;
using ArbiPache.Core.Agent;
using ArbiPache.Core.Components;
using ArbiPache.Core.Database;
using ArbiPache.Core.IO;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Processes;
using ArbiPache.Core.Settings;

namespace ArbiPache.Core.Tests.Agent;

public sealed class DatabaseOperationsTests : IDisposable
{
    private const string Version = "11.8.9";
    private const string Password = "s3cret-pass";
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
    public async Task Install_needs_a_version_that_is_installed_and_a_real_password()
    {
        var orchestrator = await MakeAsync();

        var missing = await orchestrator.InstallDatabaseAsync(DbEngine.MySql, "8.4.13", Password, Ct);
        var weak = await orchestrator.InstallDatabaseAsync(DbEngine.MariaDb, Version, "root", Ct);

        Assert.Equal("Install MySQL 8.4.13 first.", missing.Error);
        Assert.Equal("Use a root password of at least 8 characters.", weak.Error);
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
    public async Task Settings_rows_show_my_ini_mode_file_and_probed_defaults()
    {
        var orchestrator = await InstalledAsync();
        File.WriteAllText(_ini.ModeFile(DbEngine.MariaDb), "[mysqld]\nmax_statement_time = 60\n");
        _ini.Set(DbEngine.MariaDb, new Dictionary<string, string?>
        {
            ["innodb_buffer_pool_size"] = "128M", ["slow_query_log"] = "OFF", ["max_allowed_packet"] = "64M",
        });

        var rows = (await orchestrator.GetDbSettingsAsync(Ct)).ToDictionary(r => r.Key);

        Assert.Equal(new DbSettingRow("max_connections", null, "151", rows["max_connections"].Description, false, false, false, false), rows["max_connections"]);
        Assert.Equal("127.0.0.1", rows["bind-address"].Current);
        Assert.Equal("", rows["bind-address"].Default);
        Assert.True(rows["bind-address"].Changed);
        Assert.True(rows["bind-address"].RequiresRestart);
        Assert.False(rows["character-set-server"].Changed); // set in my.ini, but to the default
        Assert.False(rows["innodb_buffer_pool_size"].Changed); // 128M is the default's 134217728 bytes
        Assert.False(rows["slow_query_log"].Changed); // OFF is FALSE
        Assert.True(rows["max_allowed_packet"].Changed);
        Assert.Equal("60", rows["max_statement_time"].Current);
        Assert.True(rows["max_statement_time"].SetByMode);
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
    public async Task Database_start_writes_the_mode_file_for_its_engine()
    {
        var orchestrator = await InstalledAsync();

        var cnf = File.ReadAllText(_ini.ModeFile(DbEngine.MariaDb)).ReplaceLineEndings("\n");
        Assert.Contains("max_statement_time = 0\n", cnf, StringComparison.Ordinal);
        Assert.Contains("wait_timeout = 28800\n", cnf, StringComparison.Ordinal);
        var rows = (await orchestrator.GetDbSettingsAsync(Ct)).ToDictionary(r => r.Key);
        Assert.True(rows["wait_timeout"].SetByMode);
        Assert.Equal("28800", rows["wait_timeout"].Current);

        File.WriteAllText(_ini.ModeFile(DbEngine.MariaDb), "[mysqld]\n");
        await orchestrator.StopAsync(ServiceId.Database, Ct);
        await orchestrator.StartAsync(ServiceId.Database, Ct);
        Assert.Contains("general_log = ON", File.ReadAllText(_ini.ModeFile(DbEngine.MariaDb)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_statement_timeout_that_is_not_seconds_stops_the_start_with_a_reason()
    {
        var orchestrator = await InstalledAsync();
        await orchestrator.StopAsync(ServiceId.Database, Ct);
        new Core.Modes.ProfileStore(_paths).SaveOverrides(ServerMode.Development, new Core.Modes.ModeProfileOverrides(
            Php: null, Apache: null, Database: new Dictionary<string, string> { ["statement_timeout"] = "soon" },
            Xdebug: null, SecurityHeaders: null, DirectoryListing: null));
        _events.Clear();

        var result = await orchestrator.StartAsync(ServiceId.Database, Ct);

        Assert.False(result.Ok);
        Assert.Equal("MariaDB did not start. The Development profile's statement_timeout must be a number of seconds, not \"soon\".", result.Error);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task Mode_init_only_and_unknown_keys_are_refused()
    {
        var orchestrator = await InstalledAsync();
        File.WriteAllText(_ini.ModeFile(DbEngine.MariaDb), "[mysqld]\nwait_timeout = 600\n");

        var mode = await orchestrator.SaveDbSettingsAsync(new Dictionary<string, string?> { ["wait_timeout"] = "100" }, Ct);
        var init = await orchestrator.SaveDbSettingsAsync(new Dictionary<string, string?> { ["lower_case_table_names"] = "2" }, Ct);
        var unknown = await orchestrator.SaveDbSettingsAsync(new Dictionary<string, string?> { ["skip-grant-tables"] = "1" }, Ct);

        Assert.Equal("wait_timeout is set by Production or Development mode. Change it on the Dev / Production page.", mode.Error);
        Assert.Equal("lower_case_table_names is fixed when the database is created.", init.Error);
        Assert.Equal("skip-grant-tables is not a setting ArbiPache manages. Edit my.ini for it.", unknown.Error);
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

        Assert.True((await orchestrator.SetDbRootPasswordAsync("another-pass", Ct)).Ok);
        Assert.EndsWith(".sql.gz", await orchestrator.BackupDatabaseAsync(Ct), StringComparison.Ordinal);
        await orchestrator.StopAsync(ServiceId.Database, Ct);

        Assert.Equal("Start the database first.", (await orchestrator.SetDbRootPasswordAsync("third-pass", Ct)).Error);
        Assert.Equal("Start the database first.", (await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.BackupDatabaseAsync(Ct))).Message);
        Assert.Equal("Use a root password of at least 8 characters.", (await orchestrator.SetDbRootPasswordAsync("short", Ct)).Error);
    }

    [Fact]
    public async Task Production_warns_when_the_database_listens_beyond_this_pc()
    {
        var store = new SettingsStore(_paths);
        store.Save(store.Load() with { Mode = ServerMode.Production });
        var orchestrator = await InstalledAsync();
        Assert.DoesNotContain(orchestrator.Status().Findings, f => f.Id == "prod-db-exposed");

        Assert.True((await orchestrator.SaveDbSettingsAsync(new Dictionary<string, string?> { ["bind-address"] = "0.0.0.0" }, Ct)).Ok);
        Assert.Contains(orchestrator.Status().Findings, f => f.Id == "prod-db-exposed");

        Assert.True((await orchestrator.SaveDbSettingsAsync(new Dictionary<string, string?> { ["bind-address"] = "127.0.0.1" }, Ct)).Ok);
        Assert.DoesNotContain(orchestrator.Status().Findings, f => f.Id == "prod-db-exposed");

        // The mode file is read after my.ini, so its value is the one the server uses.
        File.AppendAllText(_ini.ModeFile(DbEngine.MariaDb), "bind-address=0.0.0.0\n");
        await orchestrator.GetDbSettingsAsync(Ct);
        Assert.Contains(orchestrator.Status().Findings, f => f.Id == "prod-db-exposed");
    }
}
