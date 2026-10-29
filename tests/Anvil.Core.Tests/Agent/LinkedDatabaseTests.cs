using Anvil.Agent;
using Anvil.Core.Agent;
using Anvil.Core.Components;
using Anvil.Core.Database;
using Anvil.Core.Elevation;
using Anvil.Core.Paths;
using Anvil.Core.Processes;
using Anvil.Core.Settings;

namespace Anvil.Core.Tests.Agent;

/// <summary>MariaDB and MySQL servers the user installed themselves: started, stopped, moved to another port and backed up.</summary>
public sealed class LinkedDatabaseTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ap-linkdb-" + Guid.NewGuid().ToString("N")));
    private readonly FakeLinkedHost _host = new();
    private readonly HashSet<int> _busy = [];
    private readonly string _ini;
    private readonly string _bin = @"C:\Program Files\MariaDB 13.0\bin";

    public LinkedDatabaseTests()
    {
        _paths.EnsureCreated();
        new SettingsStore(_paths).Save(new AppSettings { StartServicesOnLaunch = false });
        _ini = Path.Combine(_paths.Root, "my.ini");
        File.WriteAllText(_ini, "[mysqld]\r\nport=3307\r\n");
        _host.Folders[_bin] = new DbFolderInfo(DbEngine.MariaDb, "13.0.2", _bin, "MariaDB", _ini, 3307);
    }

    public void Dispose()
    {
        try { Directory.Delete(_paths.Root, recursive: true); } catch (IOException) { }
    }

    private async Task<Orchestrator> MakeAsync()
    {
        var orchestrator = new Orchestrator(_paths, new SettingsStore(_paths), new FakeTester(), new FakeApache([]), _ => [],
            new ComponentCatalog([], new ShippedCatalogProvider(_paths)),
            new ComponentInstaller(_paths, new Downloader(new HttpClient()), _ => null), new FakePools([]), port => !_busy.Contains(port),
            linkedDbs: _host);
        await orchestrator.InitializeAsync(TestContext.Current.CancellationToken);
        return orchestrator;
    }

    private async Task<Orchestrator> LinkedAsync()
    {
        var orchestrator = await MakeAsync();
        var linked = await orchestrator.LinkDatabaseAsync(_bin, "root", "root", TestContext.Current.CancellationToken);
        Assert.True(linked.Ok, linked.Error);
        _host.Granted.Add("MariaDB");
        return orchestrator;
    }

    [Fact]
    public async Task Linking_a_server_run_by_a_service_asks_once_for_starting_and_stopping_it()
    {
        var orchestrator = await MakeAsync();

        var result = await orchestrator.LinkDatabaseAsync(_bin, "root", "root", TestContext.Current.CancellationToken);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(new DbServiceAccessRequest("MariaDB", Grant: true), result.Elevation);
        Assert.Equal([new LinkedDbInfo("mariadb", DbEngine.MariaDb, "13.0.2", _bin, "MariaDB", 3307, "root", ProcessState.Running, CanControl: false)],
            orchestrator.GetLinkedDatabases());
        Assert.Equal("root", new LinkedDatabaseStore(_paths).Password("mariadb"));
    }

    [Fact]
    public async Task A_password_the_running_server_refuses_is_not_kept()
    {
        _host.Password = "secret";
        var orchestrator = await MakeAsync();

        var result = await orchestrator.LinkDatabaseAsync(_bin, "root", "root", TestContext.Current.CancellationToken);

        Assert.Equal("root could not log in to MariaDB 13.0.2 on port 3307 with that password.", result.Error);
        Assert.Empty(orchestrator.GetLinkedDatabases());
    }

    [Fact]
    public async Task A_stopped_server_is_linked_without_a_login_check()
    {
        _host.Password = "secret";
        _host.Running.Clear();
        var orchestrator = await MakeAsync();

        var result = await orchestrator.LinkDatabaseAsync(_bin, "root", "root", TestContext.Current.CancellationToken);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(ProcessState.Stopped, orchestrator.GetLinkedDatabases().Single().State);
    }

    [Fact]
    public async Task A_folder_is_linked_once()
    {
        var orchestrator = await LinkedAsync();

        var result = await orchestrator.LinkDatabaseAsync(_bin + "\\", "root", "root", TestContext.Current.CancellationToken);

        Assert.Equal($"MariaDB 13.0.2 in {_bin} is linked already.", result.Error);
    }

    [Fact]
    public async Task A_folder_that_is_not_a_server_says_why()
    {
        var orchestrator = await MakeAsync();

        var result = await orchestrator.LinkDatabaseAsync(@"C:\Temp", "root", "root", TestContext.Current.CancellationToken);

        Assert.Equal(@"C:\Temp has no mariadbd.exe or mysqld.exe. Choose the bin folder of a MariaDB or MySQL server.", result.Error);
    }

    [Fact]
    public async Task Starting_without_the_right_asks_for_it_first()
    {
        var ct = TestContext.Current.CancellationToken;
        var orchestrator = await LinkedAsync();
        _host.Granted.Clear();
        _host.Running.Clear();

        var asked = await orchestrator.StartLinkedDatabaseAsync("mariadb", ct);
        _host.Granted.Add("MariaDB");
        var started = await orchestrator.StartLinkedDatabaseAsync("mariadb", ct);

        Assert.False(asked.Ok);
        Assert.Equal(new DbServiceAccessRequest("MariaDB", Grant: true), asked.Elevation);
        Assert.True(started.Ok, started.Error);
        Assert.Equal(["start mariadb"], _host.Calls);
    }

    [Fact]
    public async Task Stopping_stops_it()
    {
        var orchestrator = await LinkedAsync();

        var result = await orchestrator.StopLinkedDatabaseAsync("mariadb", TestContext.Current.CancellationToken);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(["stop mariadb"], _host.Calls);
        Assert.Equal(ProcessState.Stopped, orchestrator.GetLinkedDatabases().Single().State);
    }

    [Fact]
    public async Task A_new_port_for_a_service_is_written_by_Windows_then_the_server_restarts_on_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var orchestrator = await LinkedAsync();

        var asked = await orchestrator.SetLinkedDatabasePortAsync("mariadb", 3310, ct);
        File.WriteAllText(_ini, DbPortFile.Write(File.ReadAllText(_ini), 3310)); // what the Elevator does
        var moved = await orchestrator.SetLinkedDatabasePortAsync("mariadb", 3310, ct);

        Assert.False(asked.Ok);
        Assert.Equal(new DbPortRequest("MariaDB", 3310), asked.Elevation);
        Assert.True(moved.Ok, moved.Error);
        Assert.Equal(3310, orchestrator.GetLinkedDatabases().Single().Port);
        Assert.Equal(["stop mariadb", "start mariadb"], _host.Calls);
    }

    [Fact]
    public async Task A_port_another_program_uses_is_refused()
    {
        var orchestrator = await LinkedAsync();
        _busy.Add(3306);

        var result = await orchestrator.SetLinkedDatabasePortAsync("mariadb", 3306, TestContext.Current.CancellationToken);

        Assert.Equal("Port 3306 is already in use by another program.", result.Error);
        Assert.Null(result.Elevation);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public async Task A_port_must_be_a_port(int port)
    {
        var orchestrator = await LinkedAsync();

        var result = await orchestrator.SetLinkedDatabasePortAsync("mariadb", port, TestContext.Current.CancellationToken);

        Assert.Equal("The port must be between 1 and 65535.", result.Error);
    }

    [Fact]
    public async Task A_server_Anvil_runs_takes_its_new_port_from_Anvil()
    {
        var bin = @"D:\mysql-8.4\bin";
        _host.Folders[bin] = new DbFolderInfo(DbEngine.MySql, "8.4.6", bin, null, null, 3306);
        var orchestrator = await MakeAsync();
        var linked = await orchestrator.LinkDatabaseAsync(bin, "root", "root", TestContext.Current.CancellationToken);

        var moved = await orchestrator.SetLinkedDatabasePortAsync("mysql-8.4.6", 3320, TestContext.Current.CancellationToken);

        Assert.Null(linked.Elevation);
        Assert.True(moved.Ok, moved.Error);
        Assert.Equal(3320, orchestrator.GetLinkedDatabases().Single().Port);
        Assert.True(orchestrator.GetLinkedDatabases().Single().CanControl);
    }

    [Fact]
    public async Task Unlinking_takes_the_right_back_and_forgets_the_password()
    {
        var orchestrator = await LinkedAsync();

        var result = await orchestrator.UnlinkDatabaseAsync("mariadb", TestContext.Current.CancellationToken);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(new DbServiceAccessRequest("MariaDB", Grant: false), result.Elevation);
        Assert.Empty(orchestrator.GetLinkedDatabases());
        Assert.Null(new LinkedDatabaseStore(_paths).Password("mariadb"));
        Assert.Empty(_host.Calls);
    }

    [Fact]
    public async Task A_backup_logs_in_with_the_kept_password()
    {
        var orchestrator = await LinkedAsync();

        var file = await orchestrator.BackupLinkedDatabaseAsync("mariadb", TestContext.Current.CancellationToken);

        Assert.Equal(@"D:\backups\mariadb.sql.gz", file);
        Assert.Equal(["backup mariadb as root with root"], _host.Calls);
    }
}

internal sealed class FakeLinkedHost : ILinkedDbHost
{
    public Dictionary<string, DbFolderInfo> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Running { get; } = ["mariadb", "mysql-8.4.6"];
    public HashSet<string> Granted { get; } = [];
    public List<string> Calls { get; } = [];
    public string Password { get; set; } = "root";

    public DbFolderInfo Inspect(string binDir) =>
        Folders.TryGetValue(Path.TrimEndingDirectorySeparator(binDir), out var found) ? found
            : throw new ArgumentException($"{binDir} has no mariadbd.exe or mysqld.exe. Choose the bin folder of a MariaDB or MySQL server.");

    public ProcessState State(LinkedDatabase server) => Running.Contains(server.Id) ? ProcessState.Running : ProcessState.Stopped;

    public bool MayControl(LinkedDatabase server) => server.Service is null || Granted.Contains(server.Service);

    public Task StartAsync(LinkedDatabase server, CancellationToken ct)
    {
        Calls.Add($"start {server.Id}");
        Running.Add(server.Id);
        return Task.CompletedTask;
    }

    public Task StopAsync(LinkedDatabase server, string? password, CancellationToken ct)
    {
        Calls.Add($"stop {server.Id}");
        Running.Remove(server.Id);
        return Task.CompletedTask;
    }

    public Task<bool> CanLogInAsync(LinkedDatabase server, string password, CancellationToken ct) => Task.FromResult(password == Password);

    public Task<string> BackupAsync(LinkedDatabase server, string password, CancellationToken ct)
    {
        Calls.Add($"backup {server.Id} as {server.User} with {password}");
        return Task.FromResult($@"D:\backups\{server.Id}.sql.gz");
    }
}
