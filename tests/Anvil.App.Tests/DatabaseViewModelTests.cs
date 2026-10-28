using Anvil.App.Services;
using Anvil.App.Tests.Support;
using Anvil.App.ViewModels;
using Anvil.Core.Agent;
using Anvil.Core.Components;
using Anvil.Core.Database;
using Anvil.Core.Paths;
using Anvil.Core.Processes;
using Anvil.Core.Settings;

namespace Anvil.App.Tests;

public sealed class DatabaseViewModelTests
{
    private static readonly AppPaths Paths = new(@"D:\Anvil");
    private readonly FakeAgentApi _api = new();

    public DatabaseViewModelTests()
    {
        _api.Settings = _api.Settings with { DbEngine = DbEngine.MariaDb, DbVersion = "11.8.9" };
        _api.Status = _api.Status with { Services = [Db(ProcessState.Running)] };
        _api.DbRows.AddRange(
        [
            new("innodb_buffer_pool_size", "1G", "134217728", "Memory for table and index data", true, false, false, false),
            new("max_connections", null, "151", "Most clients connected at once", false, false, false, false),
            new("max_allowed_packet", "64M", "16777216", "Largest single query or row", true, false, false, false),
            new("max_statement_time", "60", "0", "Seconds before a query is stopped", true, true, false, false),
            new("lower_case_table_names", null, "1", "How table names are stored", false, false, false, true),
            new("slow_query_log", "OFF", "OFF", "Log queries slower than long_query_time", false, false, false, false),
        ]);
    }

    private static ServiceStatus Db(ProcessState state, string? detail = null) => new(ServiceId.Database, state, "MariaDB 11.8.9", detail);

    private DatabaseViewModel Make() => new(_api, Paths, new EditorLauncher(() => "auto", new NoRegistry(), _ => false, ""), ui: a => a());

    private static ComponentRelease Release(ComponentKind kind, string version, bool lts = false, string? notes = null) =>
        new(kind, version, new Uri($"https://archive.mariadb.org/mariadb-{version}/winx64-packages/mariadb-{version}-winx64.zip"), new string('a', 64),
            Lts: lts, Notes: notes);

    private async Task<DatabaseViewModel> LoadedAsync()
    {
        var vm = Make();
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        return vm;
    }

    private static DbSettingItem Row(DatabaseViewModel vm, string key) => vm.Rows.Single(r => r.Key == key);

    [Fact]
    public async Task Changed_rows_counted()
    {
        var vm = await LoadedAsync();

        Assert.Equal(6, vm.Rows.Count);
        Assert.Equal("3 changed from default", vm.ChangedText);
        Assert.True(Row(vm, "innodb_buffer_pool_size").IsChanged);
        Assert.False(Row(vm, "slow_query_log").IsChanged);
        Assert.Equal("1G", Row(vm, "innodb_buffer_pool_size").Value);
        Assert.Equal("", Row(vm, "max_connections").Value);
        Assert.Equal("151", Row(vm, "max_connections").Default);

        _api.DbRows.Clear();
        _api.DbRows.Add(new("max_connections", null, "151", "Most clients connected at once", false, false, false, false));
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Every setting is at its default", vm.ChangedText);
    }

    [Fact]
    public async Task Only_changed_filter()
    {
        var vm = await LoadedAsync();
        Assert.Equal(6, vm.VisibleRows.Count);

        vm.OnlyChanged = true;
        Assert.Equal(["innodb_buffer_pool_size", "max_allowed_packet", "max_statement_time"], vm.VisibleRows.Select(r => r.Key));

        vm.Filter = "packet";
        Assert.Equal(["max_allowed_packet"], vm.VisibleRows.Select(r => r.Key));

        vm.OnlyChanged = false;
        vm.Filter = "CLIENTS";
        Assert.Equal(["max_connections"], vm.VisibleRows.Select(r => r.Key));

        vm.Filter = " ";
        Assert.Equal(6, vm.VisibleRows.Count);
    }

    [Fact]
    public async Task Reset_sends_null()
    {
        var vm = await LoadedAsync();
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.True(Row(vm, "innodb_buffer_pool_size").ResetCommand.CanExecute(null));
        Assert.False(Row(vm, "max_connections").ResetCommand.CanExecute(null));

        Row(vm, "innodb_buffer_pool_size").ResetCommand.Execute(null);
        Row(vm, "max_connections").Value = " 300 ";
        Row(vm, "max_allowed_packet").Value = "";
        Assert.Equal("", Row(vm, "innodb_buffer_pool_size").Value);
        Assert.True(vm.SaveCommand.CanExecute(null));

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(new Dictionary<string, string?>
        {
            ["innodb_buffer_pool_size"] = null,
            ["max_allowed_packet"] = null,
            ["max_connections"] = "300",
        }, _api.DbSaved);
        Assert.Contains("save db innodb_buffer_pool_size=(default) max_allowed_packet=(default) max_connections=300", _api.Calls);
    }

    [Fact]
    public async Task Value_typed_back_to_what_was_saved_is_not_sent()
    {
        var vm = await LoadedAsync();

        Row(vm, "innodb_buffer_pool_size").Value = "2G";
        Row(vm, "innodb_buffer_pool_size").Value = "1G";

        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(Row(vm, "innodb_buffer_pool_size").IsEdited);
    }

    [Fact]
    public async Task SetByMode_rows_read_only_with_hint()
    {
        var vm = await LoadedAsync();
        var row = Row(vm, "max_statement_time");

        Assert.True(row.IsReadOnly);
        Assert.Equal("Set by Development mode. Change it on the Dev / Production page.", row.Hint);
        Assert.False(row.ResetCommand.CanExecute(null));
        Assert.Null(Row(vm, "max_connections").Hint);
        Assert.False(Row(vm, "max_connections").IsReadOnly);

        _api.Raise(_api.Status with { Mode = ServerMode.Production });
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Set by Production mode. Change it on the Dev / Production page.", Row(vm, "max_statement_time").Hint);
    }

    [Fact]
    public async Task InitOnly_rows_read_only()
    {
        var vm = await LoadedAsync();
        var row = Row(vm, "lower_case_table_names");

        Assert.True(row.IsReadOnly);
        Assert.Equal("Fixed when the database is created.", row.Hint);
        Assert.False(row.ResetCommand.CanExecute(null));
    }

    [Fact]
    public async Task Save_and_restart_reports_the_outcome_and_reloads()
    {
        var vm = await LoadedAsync();
        Assert.Equal("Save and restart MariaDB", vm.SaveLabel);

        Row(vm, "max_connections").Value = "300";
        _api.NextResult = ApplyResult.Failure("MariaDB did not start with these settings, so they were put back. Unknown suffix 'x'.");
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(vm.ResultOk);
        Assert.Equal("MariaDB did not start with these settings, so they were put back. Unknown suffix 'x'.", vm.ResultText);
        Assert.Equal("300", Row(vm, "max_connections").Value);

        _api.NextResult = ApplyResult.Success();
        _api.DbRows[1] = _api.DbRows[1] with { Current = "300", Changed = true };
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.ResultOk);
        Assert.Equal("Saved. MariaDB restarted with the new settings.", vm.ResultText);
        Assert.Equal("4 changed from default", vm.ChangedText);
        Assert.False(Row(vm, "max_connections").IsEdited);

        _api.Raise(_api.Status with { Services = [Db(ProcessState.Stopped)] });
        Row(vm, "max_connections").Value = "400";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Saved. MariaDB uses them the next time it starts.", vm.ResultText);
    }

    [Fact]
    public async Task Status_line_and_data_folder_follow_the_agent()
    {
        var vm = await LoadedAsync();

        Assert.True(vm.ShowsActive);
        Assert.False(vm.ShowsSetup);
        Assert.Equal("MariaDB 11.8.9 · Running on 127.0.0.1:3306", vm.StatusText);
        Assert.Equal(@"D:\Anvil\data\mariadb\data", vm.DataFolder);
        Assert.Equal("Stop", vm.StartStopLabel);

        _api.Raise(_api.Status with { Services = [Db(ProcessState.Failed, "MariaDB did not start. Port in use.")] });
        Assert.Equal("MariaDB 11.8.9 · Failed", vm.StatusText);
        Assert.Equal("MariaDB did not start. Port in use.", vm.ResultText);
        Assert.False(vm.ResultOk);
        Assert.Equal("Start", vm.StartStopLabel);

        await vm.StartStopCommand.ExecuteAsync(null);
        Assert.Contains("start Database", _api.Calls);
    }

    [Fact]
    public async Task Stop_stops_the_database()
    {
        var vm = await LoadedAsync();

        await vm.StartStopCommand.ExecuteAsync(null);

        Assert.Equal(["stop Database"], _api.Calls);
    }

    [Fact]
    public async Task First_install_downloads_initializes_and_shows_settings()
    {
        _api.Settings = _api.Settings with { DbEngine = null, DbVersion = null };
        _api.Status = _api.Status with { Services = [new ServiceStatus(ServiceId.Database, ProcessState.Stopped, null, null)] };
        _api.Available.Add(Release(ComponentKind.MariaDb, "12.3.1"));
        _api.Available.Add(Release(ComponentKind.MariaDb, "11.8.9", lts: true));
        _api.Available.Add(Release(ComponentKind.MariaDb, "10.6.25", lts: true, notes: "End of life"));
        var vm = await LoadedAsync();

        Assert.False(vm.ShowsActive);
        Assert.True(vm.ShowsSetup);
        Assert.Equal(DbEngine.MariaDb, vm.SelectedEngine);
        Assert.Equal("Set up MariaDB", vm.SetupTitle);
        Assert.Equal(["MariaDB 12.3.1", "MariaDB 11.8.9 · LTS", "MariaDB 10.6.25 · LTS · End of life"], vm.InstallChoices.Select(c => c.Label));
        Assert.Equal("11.8.9", vm.SelectedInstall?.Release.Version);
        Assert.Empty(vm.Rows);

        vm.RootPassword = "short";
        Assert.False(vm.InstallCommand.CanExecute(null));
        vm.RootPassword = "long enough";
        Assert.True(vm.InstallCommand.CanExecute(null));

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Equal(["install 11.8.9", "install db MariaDb 11.8.9"], _api.Calls);
        Assert.True(vm.ShowsActive);
        Assert.Equal("", vm.RootPassword);
        Assert.Equal(6, vm.Rows.Count);
        Assert.True(vm.ResultOk);
        Assert.Equal("MariaDB 11.8.9 is set up.", vm.ResultText);
    }

    [Fact]
    public async Task Installed_version_is_offered_first_and_not_downloaded_again()
    {
        _api.Settings = _api.Settings with { DbEngine = null, DbVersion = null };
        _api.Available.Add(Release(ComponentKind.MySql, "8.4.13", lts: true));
        _api.Installed.Add(new InstalledComponent(ComponentKind.MySql, "8.4.12", @"D:\Anvil\runtime\mysql\8.4.12", new string('a', 64), DateTimeOffset.Now));
        var vm = await LoadedAsync();

        vm.SelectEngineCommand.Execute(DbEngine.MySql);
        Assert.Equal(["MySQL 8.4.13 · LTS", "MySQL 8.4.12 · installed"], vm.InstallChoices.Select(c => c.Label));
        Assert.Equal("8.4.12", vm.SelectedInstall?.Release.Version);

        vm.RootPassword = "long enough";
        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Equal(["install db MySql 8.4.12"], _api.Calls);
    }

    [Fact]
    public async Task Failed_install_keeps_the_password_and_says_why()
    {
        _api.Settings = _api.Settings with { DbEngine = null, DbVersion = null };
        _api.Available.Add(Release(ComponentKind.MariaDb, "11.8.9", lts: true));
        var vm = await LoadedAsync();
        vm.RootPassword = "not the old one";
        _api.NextResult = ApplyResult.Failure("That is not the root password of the MariaDB database in D:\\Anvil\\data\\mariadb\\data.");

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.False(vm.ResultOk);
        Assert.StartsWith("That is not the root password", vm.ResultText, StringComparison.Ordinal);
        Assert.Equal("not the old one", vm.RootPassword);
        Assert.True(vm.ShowsSetup);
    }

    [Fact]
    public async Task Other_engine_offers_a_switch_and_says_the_active_one_stops()
    {
        _api.Available.Add(Release(ComponentKind.MySql, "8.4.13", lts: true));
        var vm = await LoadedAsync();

        vm.SelectEngineCommand.Execute(DbEngine.MySql);

        Assert.True(vm.IsMySqlSelected);
        Assert.False(vm.IsMariaDbSelected);
        Assert.True(vm.ShowsSetup);
        Assert.Equal("Switch to MySQL", vm.SetupTitle);
        Assert.Contains("MariaDB stops: one database runs at a time.", vm.SetupNote, StringComparison.Ordinal);

        vm.SelectEngineCommand.Execute(DbEngine.MariaDb);
        Assert.True(vm.ShowsActive);
    }

    [Fact]
    public async Task Back_up_all_reports_the_file()
    {
        var vm = await LoadedAsync();

        await vm.BackupCommand.ExecuteAsync(null);

        Assert.Contains("backup db", _api.Calls);
        Assert.True(vm.ResultOk);
        Assert.Equal(@"Every database was backed up to D:\Anvil\data\backups\db\2026-10-27-120000.sql.gz", vm.ResultText);
    }

    [Fact]
    public async Task Change_root_password_needs_eight_characters()
    {
        var vm = await LoadedAsync();
        Assert.False(vm.IsChangingPassword);
        vm.ChangeRootPasswordCommand.Execute(null);
        Assert.True(vm.IsChangingPassword);

        vm.NewRootPassword = "1234567";
        Assert.False(vm.ApplyRootPasswordCommand.CanExecute(null));
        vm.NewRootPassword = "12345678";
        await vm.ApplyRootPasswordCommand.ExecuteAsync(null);

        Assert.Contains("db password", _api.Calls);
        Assert.Equal("Root password changed. Update it in the apps that log in as root.", vm.ResultText);
        Assert.Equal("", vm.NewRootPassword);
        Assert.False(vm.IsChangingPassword);
    }

    [Fact]
    public async Task Agent_errors_become_messages()
    {
        var vm = await LoadedAsync();
        _api.BackupError = new InvalidOperationException("Start the database first.");

        await vm.BackupCommand.ExecuteAsync(null);

        Assert.False(vm.ResultOk);
        Assert.Equal("Nothing was backed up. Start the database first.", vm.ResultText);
    }
}
