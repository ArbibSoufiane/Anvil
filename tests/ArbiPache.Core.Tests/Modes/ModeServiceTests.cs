using ArbiPache.Agent;
using ArbiPache.Core.Agent;
using ArbiPache.Core.Apache;
using ArbiPache.Core.Components;
using ArbiPache.Core.Database;
using ArbiPache.Core.Elevation;
using ArbiPache.Core.IO;
using ArbiPache.Core.Modes;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Processes;
using ArbiPache.Core.Settings;
using ArbiPache.Core.Tests.Agent;

namespace ArbiPache.Core.Tests.Modes;

/// <summary>Switching Development/Production through the Orchestrator: preview, backup, write, test, restart, roll back.</summary>
public sealed class ModeServiceTests : IDisposable
{
    private const string Apache = "2.4.69";
    private const string Php = "8.5.12";
    private const string Db = "11.8.9";

    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ap-mode-" + Guid.NewGuid().ToString("N")));
    private readonly FakeTester _tester = new();
    private readonly List<string> _events = [];
    private readonly FakeApache _apache;
    private readonly FakePools _pools;
    private readonly FakeDb _db;

    public ModeServiceTests()
    {
        _apache = new FakeApache(_events);
        _pools = new FakePools(_events);
        _db = new FakeDb(_events);
        var runtime = _paths.ApacheRuntime(Apache);
        Directory.CreateDirectory(Path.Combine(runtime, "conf"));
        Directory.CreateDirectory(Path.Combine(runtime, "bin"));
        File.WriteAllText(Path.Combine(runtime, "conf", "httpd.conf"),
            "Define SRVROOT \"c:/Apache24\"\r\nServerRoot \"${SRVROOT}\"\r\nListen 80\r\nLoadModule authz_core_module modules/mod_authz_core.so\r\n");
        File.WriteAllText(Path.Combine(runtime, "bin", "httpd.exe"), "");
        Directory.CreateDirectory(_paths.Data);
        TestPhp.Install(_paths, Php);
        Register(new InstalledComponent(ComponentKind.MariaDb, Db, _paths.DbRuntime(DbEngine.MariaDb, Db), new string('a', 64), DateTimeOffset.UtcNow));
        new SettingsStore(_paths).Save(new AppSettings { ActiveApache = Apache, DefaultPhp = Php, StartServicesOnLaunch = false });
    }

    public void Dispose()
    {
        try { Directory.Delete(_paths.Root, recursive: true); } catch (IOException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void Register(InstalledComponent c)
    {
        var registry = Path.Combine(_paths.Runtime, "installed.json");
        var all = File.Exists(registry) ? JsonFiles.Read<List<InstalledComponent>>(registry)! : [];
        all.Add(c);
        JsonFiles.Write(registry, all);
    }

    /// <summary>Apache, the PHP pool and MariaDB running in Development.</summary>
    private async Task<Orchestrator> RunningAsync()
    {
        var orchestrator = new Orchestrator(_paths, new SettingsStore(_paths), _tester, _apache, _ => [],
            new ComponentCatalog([], new ShippedCatalogProvider(_paths)),
            new ComponentInstaller(_paths, new Downloader(new HttpClient()), _ => null), _pools, _ => true, db: _db);
        await orchestrator.InitializeAsync(Ct);
        Assert.True((await orchestrator.InstallDatabaseAsync(DbEngine.MariaDb, Db, "s3cret-pass", Ct)).Ok);
        Assert.True((await orchestrator.StartAsync(null, Ct)).Ok);
        _events.Clear();
        return orchestrator;
    }

    private string PhpModeIni => PhpModeIniRenderer.FilePath(_paths, Php);
    private string ModeConf => Path.Combine(_paths.Conf, "mode.conf");
    private string DbModeCnf => new MyIni(_paths).ModeFile(DbEngine.MariaDb);

    /// <summary>Every file ArbiPache generates for Apache, PHP and the database, by path.</summary>
    private Dictionary<string, byte[]> Generated() =>
        Directory.GetFiles(_paths.Conf, "*", SearchOption.AllDirectories).Append(PhpModeIni).Append(DbModeCnf)
            .ToDictionary(f => f, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task Preview_lists_display_errors_on_to_off()
    {
        var orchestrator = await RunningAsync();
        var before = Generated();

        var preview = await orchestrator.PreviewModeAsync(ServerMode.Production, Ct);

        Assert.Equal((ServerMode.Development, ServerMode.Production), (preview.From, preview.To));
        Assert.Contains(new SettingChange("PHP", @"php\8.5.12\conf.d\99-arbipache-mode.ini", "display_errors", "On", "Off"), preview.Changes);
        Assert.Contains(new SettingChange("PHP", @"php\8.5.12\conf.d\99-arbipache-mode.ini", "disable_functions", "",
            "exec,shell_exec,system,passthru,proc_open,popen"), preview.Changes);
        Assert.Contains(new SettingChange("Apache", @"conf\mode.conf", "Timeout", "300", "60"), preview.Changes);
        Assert.Contains(new SettingChange("Apache", @"conf\php-pools.conf", "PhpBackendTimeout", "300", "60"), preview.Changes);
        Assert.Contains(new SettingChange("Apache", @"conf\vhosts", "Unknown Host", "htdocs (private ranges)", "403, empty"), preview.Changes);
        Assert.Contains(new SettingChange("MariaDB", @"mariadb\mode.cnf", "max_statement_time", "0", "60"), preview.Changes);
        Assert.Contains(new SettingChange("Windows Firewall", "", "Web ports open to", "Your network", "The internet"), preview.Changes);
        Assert.DoesNotContain(preview.Changes, c => c.Key == "log_errors");
        Assert.Equal(Path.Combine(_paths.Backups, preview.BackupPreviewPath[(_paths.Backups.Length + 1)..]), preview.BackupPreviewPath);
        Assert.Equal(ServerMode.Development, orchestrator.Settings.Mode);
        Assert.Equal(before, Generated());
        Assert.Empty(_events);
    }

    [Fact]
    public async Task An_Apache_config_apply_keeps_the_database_mode_file()
    {
        // RunningAsync starts the database, then applies Apache's configuration.
        await RunningAsync();

        Assert.Contains("max_statement_time = 0", File.ReadAllText(DbModeCnf), StringComparison.Ordinal);
    }

    private static ModeProfileOverrides PhpOverride(string key, string value) =>
        new(Php: new Dictionary<string, string> { [key] = value }, Apache: null, Database: null, Xdebug: null, SecurityHeaders: null, DirectoryListing: null);

    [Fact]
    public async Task Profiles_list_both_modes_with_the_users_values()
    {
        var orchestrator = await RunningAsync();
        new ProfileStore(_paths).SaveOverrides(ServerMode.Production, PhpOverride("memory_limit", "512M"));

        var profiles = await orchestrator.GetModeProfilesAsync(Ct);

        Assert.Equal([ServerMode.Development, ServerMode.Production], profiles.Select(p => p.Mode));
        Assert.Equal("1G", profiles[0].Php["memory_limit"]);
        Assert.Equal("512M", profiles[1].Php["memory_limit"]);
    }

    [Fact]
    public async Task Saving_the_other_modes_profile_only_stores_it()
    {
        var orchestrator = await RunningAsync();
        var before = Generated();

        var result = await orchestrator.SaveModeProfileAsync(ServerMode.Production, PhpOverride("memory_limit", "512M"), Ct);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("512M", new ProfileStore(_paths).Get(ServerMode.Production).Php["memory_limit"]);
        Assert.Equal(before, Generated());
        Assert.Empty(_events);
    }

    [Fact]
    public async Task Saving_the_active_profile_rewrites_its_files_and_restarts_what_changed()
    {
        var orchestrator = await RunningAsync();

        var result = await orchestrator.SaveModeProfileAsync(ServerMode.Development, PhpOverride("memory_limit", "2G"), Ct);

        Assert.True(result.Ok, result.Error);
        Assert.Null(result.RequiredFirewall);
        Assert.Contains("memory_limit = 2G", File.ReadAllText(PhpModeIni), StringComparison.Ordinal);
        Assert.Equal(["php85"], _pools.Restarted);
        Assert.Equal(0, _apache.Restarts);
        Assert.DoesNotContain(_events, e => e.StartsWith("db ", StringComparison.Ordinal));
        var snapshot = Assert.Single(new BackupService(_paths, TimeProvider.System).List());
        Assert.Contains("development.json", File.ReadAllText(Path.Combine(snapshot, "manifest.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_profile_edit_puts_the_old_values_back()
    {
        var orchestrator = await RunningAsync();
        var before = Generated();
        _tester.Next = new ConfigTestResult(false, "AH00526: Syntax error on line 2 of mode.conf");
        var overrides = new ModeProfileOverrides(null, new Dictionary<string, string> { ["Timeout"] = "ten" }, null, null, null, null);

        var result = await orchestrator.SaveModeProfileAsync(ServerMode.Development, overrides, Ct);

        Assert.False(result.Ok);
        Assert.Equal("Apache rejected the configuration. The Development profile keeps its values, and its files were put back.", result.Error);
        Assert.False(File.Exists(new ProfileStore(_paths).FileFor(ServerMode.Development)));
        Assert.Equal(before, Generated());
        Assert.Empty(_events);
    }

    [Fact]
    public async Task A_profile_value_with_a_line_break_is_refused()
    {
        var orchestrator = await RunningAsync();

        var result = await orchestrator.SaveModeProfileAsync(ServerMode.Production, PhpOverride("memory_limit", "1G\nauto_prepend_file = x"), Ct);

        Assert.False(result.Ok);
        Assert.Equal("memory_limit cannot contain line breaks.", result.Error);
        Assert.False(File.Exists(new ProfileStore(_paths).FileFor(ServerMode.Production)));
    }

    [Fact]
    public async Task Stopped_services_get_the_new_files_and_no_restart()
    {
        var orchestrator = await RunningAsync();
        Assert.True((await orchestrator.StopAsync(null, Ct)).Ok);
        _events.Clear();

        var preview = await orchestrator.PreviewModeAsync(ServerMode.Production, Ct);
        var result = await orchestrator.ApplyModeAsync(ServerMode.Production, new ModeOptions(KeepXdebug: false), Ct);

        Assert.Empty(preview.Restarts);
        Assert.True(result.Ok, result.Error);
        Assert.Contains("display_errors = Off", File.ReadAllText(PhpModeIni), StringComparison.Ordinal);
        Assert.Contains("Timeout 60", File.ReadAllText(ModeConf), StringComparison.Ordinal);
        Assert.Contains("max_statement_time = 60", File.ReadAllText(DbModeCnf), StringComparison.Ordinal);
        Assert.Empty(_pools.Restarted);
        Assert.Equal(0, _apache.Restarts);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task Preview_restarts_only_changed_services()
    {
        var orchestrator = await RunningAsync();
        Assert.Equal([ServiceId.Php, ServiceId.Apache, ServiceId.Database], (await orchestrator.PreviewModeAsync(ServerMode.Production, Ct)).Restarts);

        // Production's PHP edited to match Development's: the pools have nothing to restart for.
        new ProfileStore(_paths).SaveOverrides(ServerMode.Production, new ModeProfileOverrides(
            Php: BuiltInProfiles.Development.Php, Apache: null, Database: null, Xdebug: null, SecurityHeaders: null, DirectoryListing: null));
        await orchestrator.StopAsync(ServiceId.Database, Ct);
        var preview = await orchestrator.PreviewModeAsync(ServerMode.Production, Ct);

        Assert.Equal([ServiceId.Apache], preview.Restarts);
        Assert.DoesNotContain(preview.Changes, c => c.Where == "PHP");
        Assert.Contains(preview.Changes, c => c.Where == "MariaDB");
    }

    [Fact]
    public async Task Apply_writes_backs_up_and_restarts_what_changed()
    {
        var orchestrator = await RunningAsync();

        var result = await orchestrator.ApplyModeAsync(ServerMode.Production, new ModeOptions(KeepXdebug: false), Ct);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(ServerMode.Production, orchestrator.Settings.Mode);
        Assert.Equal(ServerMode.Production, new SettingsStore(_paths).Load().Mode);
        Assert.Contains("display_errors = Off", File.ReadAllText(PhpModeIni), StringComparison.Ordinal);
        Assert.Contains("Timeout 60", File.ReadAllText(ModeConf), StringComparison.Ordinal);
        Assert.Contains("max_statement_time = 60", File.ReadAllText(DbModeCnf), StringComparison.Ordinal);
        Assert.Equal(["php85"], _pools.Restarted);
        Assert.Equal(1, _apache.Restarts);
        Assert.Equal(["db stop MariaDb 11.8.9", "db start MariaDb 11.8.9"], _events.Where(e => e.StartsWith("db ", StringComparison.Ordinal)));
        var snapshot = Assert.Single(new BackupService(_paths, TimeProvider.System).List());
        var manifest = File.ReadAllText(Path.Combine(snapshot, "manifest.json"));
        Assert.Contains("mode.conf", manifest, StringComparison.Ordinal);
        Assert.Contains("99-arbipache-mode.ini", manifest, StringComparison.Ordinal);
        Assert.Contains("mode.cnf", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_failure_restores_snapshot()
    {
        var orchestrator = await RunningAsync();
        var before = Generated();
        _tester.Next = new ConfigTestResult(false, "AH00526: Syntax error on line 3 of mode.conf");

        var result = await orchestrator.ApplyModeAsync(ServerMode.Production, new ModeOptions(KeepXdebug: false), Ct);

        Assert.False(result.Ok);
        Assert.Equal("Apache rejected the configuration. Development mode stays, and its files were put back.", result.Error);
        Assert.Equal("AH00526: Syntax error on line 3 of mode.conf", result.ConfigTestOutput);
        Assert.Equal(before, Generated());
        Assert.Equal(ServerMode.Development, new SettingsStore(_paths).Load().Mode);
        Assert.Empty(_pools.Restarted);
        Assert.Equal(0, _apache.Restarts);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task A_service_that_will_not_restart_puts_everything_back()
    {
        var orchestrator = await RunningAsync();
        var before = Generated();
        _db.FailNextStart = "Unknown variable 'max_statement_time'";

        var result = await orchestrator.ApplyModeAsync(ServerMode.Production, new ModeOptions(KeepXdebug: false), Ct);

        Assert.False(result.Ok);
        Assert.Equal("MariaDB did not restart in Production mode, so Development mode and its files were put back. Unknown variable 'max_statement_time'",
            result.Error);
        Assert.Equal(before, Generated());
        Assert.Equal(ServerMode.Development, orchestrator.Settings.Mode);
        // The pools and Apache that restarted with Production's files restart again with Development's; the database starts again.
        Assert.Equal(["php85", "php85"], _pools.Restarted);
        Assert.Equal(2, _apache.Restarts);
        Assert.Equal(ProcessState.Running, _db.Status(DbEngine.MariaDb)?.State);
    }

    [Fact]
    public async Task Switch_and_back_restores_files_byte_identical()
    {
        var orchestrator = await RunningAsync();
        var before = Generated();

        Assert.True((await orchestrator.ApplyModeAsync(ServerMode.Production, new ModeOptions(KeepXdebug: false), Ct)).Ok);
        Assert.NotEqual(before[ModeConf], File.ReadAllBytes(ModeConf));
        Assert.True((await orchestrator.ApplyModeAsync(ServerMode.Development, new ModeOptions(KeepXdebug: false), Ct)).Ok);

        Assert.Equal(before, Generated());
        Assert.Equal(2, new BackupService(_paths, TimeProvider.System).List().Count);
    }

    [Fact]
    public async Task Production_requires_internet_firewall()
    {
        var orchestrator = await RunningAsync();

        var production = await orchestrator.ApplyModeAsync(ServerMode.Production, new ModeOptions(KeepXdebug: false), Ct);
        var development = await orchestrator.ApplyModeAsync(ServerMode.Development, new ModeOptions(KeepXdebug: false), Ct);

        Assert.Equal(FirewallScope.Internet, production.RequiredFirewall);
        Assert.Equal(FirewallScope.LocalNetwork, development.RequiredFirewall);
    }

    [Fact]
    public async Task Keep_xdebug_keeps_it_loaded_in_production()
    {
        var folder = Path.Combine(_paths.Runtime, "xdebug", "3.5.3-php8.5");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "php_xdebug-3.5.3-8.5-nts-vs17-x86_64.dll"), "");
        Register(new InstalledComponent(ComponentKind.Xdebug, "3.5.3", folder, new string('b', 64), DateTimeOffset.UtcNow, AppliesTo: "8.5"));
        var orchestrator = await RunningAsync();
        Assert.Contains("zend_extension", File.ReadAllText(PhpModeIni), StringComparison.Ordinal);

        var keep = await orchestrator.PreviewModeAsync(ServerMode.Production, Ct);
        Assert.Contains(new SettingChange("PHP", @"php\8.5.12\conf.d\99-arbipache-mode.ini", "xdebug", "loaded", "not loaded"), keep.Changes);

        Assert.True((await orchestrator.ApplyModeAsync(ServerMode.Production, new ModeOptions(KeepXdebug: true), Ct)).Ok);
        Assert.Contains("zend_extension", File.ReadAllText(PhpModeIni), StringComparison.Ordinal);
        Assert.True(new SettingsStore(_paths).Load().XdebugInProduction);

        // A later pool restart renders the same file.
        await orchestrator.RestartAsync(ServiceId.Php, Ct);
        Assert.Contains("zend_extension", File.ReadAllText(PhpModeIni), StringComparison.Ordinal);

        Assert.True((await orchestrator.ApplyModeAsync(ServerMode.Production, new ModeOptions(KeepXdebug: false), Ct)).Ok);
        Assert.DoesNotContain("zend_extension", File.ReadAllText(PhpModeIni), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Applying_the_current_mode_changes_nothing()
    {
        var orchestrator = await RunningAsync();

        var result = await orchestrator.ApplyModeAsync(ServerMode.Development, new ModeOptions(KeepXdebug: false), Ct);

        Assert.True(result.Ok);
        Assert.Empty(_events);
        Assert.Empty(_pools.Restarted);
        Assert.Empty(new BackupService(_paths, TimeProvider.System).List());
    }

    [Fact]
    public async Task Saving_settings_cannot_change_the_mode()
    {
        var orchestrator = await RunningAsync();
        var stale = orchestrator.Settings;
        Assert.True((await orchestrator.ApplyModeAsync(ServerMode.Production, new ModeOptions(KeepXdebug: true), Ct)).Ok);

        Assert.True((await orchestrator.SaveSettingsAsync(stale with { CloseToTray = false }, Ct)).Ok);

        Assert.Equal(ServerMode.Production, orchestrator.Settings.Mode);
        Assert.True(orchestrator.Settings.XdebugInProduction);
        Assert.False(orchestrator.Settings.CloseToTray);
    }
}
