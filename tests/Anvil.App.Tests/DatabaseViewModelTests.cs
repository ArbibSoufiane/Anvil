using Anvil.App.Tests.Support;
using Anvil.App.ViewModels;
using Anvil.Core.Agent;
using Anvil.Core.Database;
using Anvil.Core.Processes;

namespace Anvil.App.Tests;

public sealed class DatabaseViewModelTests
{
    private readonly FakeAgentApi _api = new();

    [Fact]
    public async Task The_page_lists_the_servers_the_user_linked()
    {
        _api.LinkedDbs.Add(new LinkedDbInfo("mariadb", DbEngine.MariaDb, "13.0.2", @"C:\Program Files\MariaDB 13.0\bin", "MariaDB", 3307, "root",
            ProcessState.Running, CanControl: true));
        var linked = new LinkedDatabasesViewModel(_api, new FakeElevator(), new FakeFolderPicker());
        var vm = new DatabaseViewModel(linked);

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Same(linked, vm.Linked);
        Assert.Equal(["MariaDB 13.0.2"], vm.Linked.Servers.Select(s => s.Title));
    }

    [Fact]
    public void The_guide_opens_each_download_page()
    {
        var opened = new List<string>();
        var vm = new DatabaseViewModel(new LinkedDatabasesViewModel(_api, new FakeElevator(), new FakeFolderPicker()), opened.Add);

        foreach (var download in DatabaseViewModel.Downloads) vm.OpenDownloadCommand.Execute(download);

        Assert.Equal(["https://mariadb.org/download/", "https://dev.mysql.com/downloads/mysql/"], opened);
        Assert.Contains(DatabaseViewModel.Steps, s => s.Contains("bin folder", StringComparison.Ordinal));
    }
}
