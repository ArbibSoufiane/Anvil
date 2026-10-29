using Anvil.App.Tests.Support;
using Anvil.App.ViewModels;
using Anvil.Core.Agent;
using Anvil.Core.Database;
using Anvil.Core.Elevation;
using Anvil.Core.Processes;

namespace Anvil.App.Tests;

public sealed class LinkedDatabasesViewModelTests
{
    private const string Bin = @"C:\Program Files\MariaDB 13.0\bin";
    private static readonly DbServiceAccessRequest Grant = new("MariaDB", Grant: true);

    private readonly FakeAgentApi _api = new();
    private readonly FakeElevator _elevator = new();
    private readonly FakeFolderPicker _picker = new() { Folder = Bin };

    public LinkedDatabasesViewModelTests() =>
        _api.LinkedDbs.Add(new LinkedDbInfo("mariadb", DbEngine.MariaDb, "13.0.2", Bin, "MariaDB", 3307, "root", ProcessState.Running, CanControl: true));

    private async Task<LinkedDatabasesViewModel> LoadedAsync()
    {
        var vm = new LinkedDatabasesViewModel(_api, _elevator, _picker);
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        return vm;
    }

    [Fact]
    public async Task Each_linked_server_shows_how_it_runs()
    {
        _api.LinkedDbs.Add(new LinkedDbInfo("mysql-8.4.6", DbEngine.MySql, "8.4.6", @"D:\mysql\bin", null, 3320, "root", ProcessState.Stopped, CanControl: true));

        var vm = await LoadedAsync();

        Assert.Equal(["MariaDB 13.0.2", "MySQL 8.4.6"], vm.Servers.Select(s => s.Title));
        Assert.Equal(["Windows service MariaDB · port 3307", "Run by Anvil · port 3320"], vm.Servers.Select(s => s.Detail));
        Assert.Equal(["Running", "Stopped"], vm.Servers.Select(s => s.StatusText));
        Assert.Equal("3307", vm.Servers[0].PortText);
        Assert.True(vm.HasServers);
    }

    [Fact]
    public async Task Linking_asks_Windows_once_so_start_and_stop_never_ask_again()
    {
        _api.LinkedResults.Enqueue(ApplyResult.Success() with { Elevation = Grant });
        var vm = await LoadedAsync();

        await vm.ChooseFolderCommand.ExecuteAsync(null);
        vm.LinkPassword = "root";
        await vm.LinkCommand.ExecuteAsync(null);

        Assert.Equal(Bin, vm.LinkFolder);
        Assert.Contains($"link db {Bin} root root", _api.Calls);
        Assert.Equal([Grant], _elevator.Requests);
        Assert.Equal("Linked. Start and Stop work here without Windows asking again.", vm.Message);
        Assert.False(vm.IsLinking);
    }

    [Fact]
    public async Task A_refused_link_keeps_the_form_open_with_the_reason()
    {
        _api.LinkedResults.Enqueue(ApplyResult.Failure("root could not log in to MariaDB 13.0.2 on port 3307 with that password."));
        var vm = await LoadedAsync();
        await vm.ChooseFolderCommand.ExecuteAsync(null);

        await vm.LinkCommand.ExecuteAsync(null);

        Assert.Equal("root could not log in to MariaDB 13.0.2 on port 3307 with that password.", vm.Message);
        Assert.False(vm.MessageOk);
        Assert.True(vm.IsLinking);
    }

    [Fact]
    public async Task Starting_without_the_right_gets_it_then_starts()
    {
        _api.LinkedResults.Enqueue(ApplyResult.Failure("Windows must allow you to start and stop MariaDB first.") with { Elevation = Grant });
        _api.LinkedResults.Enqueue(ApplyResult.Success());
        var vm = await LoadedAsync();

        await vm.Servers[0].StartCommand.ExecuteAsync(null);

        Assert.Equal(["start db mariadb", "start db mariadb"], _api.Calls.Where(c => c.StartsWith("start", StringComparison.Ordinal)));
        Assert.Equal([Grant], _elevator.Requests);
        Assert.True(vm.MessageOk);
    }

    [Fact]
    public async Task A_new_port_is_written_with_Windows_permission_then_applied()
    {
        _api.LinkedResults.Enqueue(ApplyResult.Failure("Windows must allow Anvil to change my.ini.") with { Elevation = new DbPortRequest("MariaDB", 3310) });
        _api.LinkedResults.Enqueue(ApplyResult.Success());
        var vm = await LoadedAsync();
        vm.Servers[0].PortText = "3310";

        await vm.Servers[0].ApplyPortCommand.ExecuteAsync(null);

        Assert.Equal(["port db mariadb 3310", "port db mariadb 3310"], _api.Calls.Where(c => c.StartsWith("port", StringComparison.Ordinal)));
        Assert.Equal([new DbPortRequest("MariaDB", 3310)], _elevator.Requests);
        Assert.Equal("MariaDB 13.0.2 listens on port 3310 now.", vm.Message);
    }

    [Fact]
    public async Task Declining_Windows_prompt_changes_nothing()
    {
        _api.LinkedResults.Enqueue(ApplyResult.Failure("Windows must allow Anvil to change my.ini.") with { Elevation = new DbPortRequest("MariaDB", 3310) });
        _elevator.Next = new ElevatedResult(false, "The operation was canceled by the user.");
        var vm = await LoadedAsync();
        vm.Servers[0].PortText = "3310";

        await vm.Servers[0].ApplyPortCommand.ExecuteAsync(null);

        Assert.Single(_api.Calls, c => c.StartsWith("port", StringComparison.Ordinal));
        Assert.Equal("Windows did not allow it: The operation was canceled by the user.", vm.Message);
        Assert.False(vm.MessageOk);
    }

    [Fact]
    public async Task A_port_must_be_a_number()
    {
        var vm = await LoadedAsync();
        vm.Servers[0].PortText = "abc";

        await vm.Servers[0].ApplyPortCommand.ExecuteAsync(null);

        Assert.Equal("Enter a port number between 1 and 65535.", vm.Message);
        Assert.DoesNotContain(_api.Calls, c => c.StartsWith("port", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unlinking_takes_the_right_back()
    {
        var revoke = new DbServiceAccessRequest("MariaDB", Grant: false);
        _api.LinkedResults.Enqueue(ApplyResult.Success() with { Elevation = revoke });
        var vm = await LoadedAsync();

        await vm.Servers[0].UnlinkCommand.ExecuteAsync(null);

        Assert.Contains("unlink db mariadb", _api.Calls);
        Assert.Equal([revoke], _elevator.Requests);
        Assert.Equal("MariaDB 13.0.2 is no longer linked. The server itself is unchanged.", vm.Message);
    }

    [Fact]
    public async Task A_backup_says_where_it_went()
    {
        var vm = await LoadedAsync();

        await vm.Servers[0].BackupCommand.ExecuteAsync(null);

        Assert.Contains("backup db mariadb", _api.Calls);
        Assert.Equal($"Backed up to {_api.BackupFile}", vm.Message);
    }
}
