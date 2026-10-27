using ArbiPache.App.Services;
using ArbiPache.App.Tests.Support;
using ArbiPache.App.ViewModels;
using ArbiPache.Core.Agent;
using ArbiPache.Core.Components;
using ArbiPache.Core.Paths;

namespace ArbiPache.App.Tests;

public sealed class UpdatesPanelTests
{
    private static readonly AppPaths Paths = new(@"D:\ArbiPache");
    private readonly FakeAgentApi _api = new();

    private static EditorLauncher Editor => new(() => "auto", new NoRegistry(), _ => false, "");

    private static ComponentRelease Release(ComponentKind k, string version) =>
        new(k, version, new Uri($"https://example.com/{k}-{version}.zip"), new string('a', 64), null);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_page_shows_the_updates_for_its_component()
    {
        _api.Offers.AddRange(
        [
            new UpdateOffer(ComponentKind.Apache, "2.4.69", "2.4.70", Release(ComponentKind.Apache, "2.4.70")),
            new UpdateOffer(ComponentKind.Php, "8.5.5", "8.5.12", null),
            new UpdateOffer(ComponentKind.MariaDb, "11.8.3", "11.8.5", Release(ComponentKind.MariaDb, "11.8.5")),
        ]);
        var apache = new ApacheViewModel(_api, Paths, Editor, ui: a => a());
        var php = new PhpViewModel(_api, Paths, Editor, ui: a => a());
        var database = new DatabaseViewModel(_api, Paths, Editor, ui: a => a());

        await apache.LoadAsync(Ct);
        await php.LoadAsync(Ct);
        await database.LoadAsync(Ct);

        var row = Assert.Single(apache.Updates.Rows);
        Assert.Equal("Apache 2.4.70 is available", row.Title);
        Assert.Equal("You use 2.4.69. ArbiPache downloads it, switches, and puts 2.4.69 back if the new version does not run.", row.Note);
        Assert.True(apache.Updates.HasRows);
        Assert.Equal("PHP 8.5.12 is available", Assert.Single(php.Updates.Rows).Title);
        Assert.Equal("You use 8.5.5. It is downloaded already: ArbiPache switches, and puts 8.5.5 back if the new version does not run.",
            Assert.Single(php.Updates.Rows).Note);
        Assert.Equal("MariaDB 11.8.5 is available", Assert.Single(database.Updates.Rows).Title);
    }

    [Fact]
    public async Task Update_downloads_then_switches()
    {
        _api.Offers.Add(new UpdateOffer(ComponentKind.Apache, "2.4.69", "2.4.70", Release(ComponentKind.Apache, "2.4.70")));
        var vm = new ApacheViewModel(_api, Paths, Editor, ui: a => a());
        await vm.LoadAsync(Ct);
        _api.Offers.Clear();

        await vm.Updates.Rows[0].UpdateCommand.ExecuteAsync(null);

        Assert.Equal(["install 2.4.70", "activate 2.4.70"], _api.Calls);
        Assert.True(vm.Updates.MessageOk);
        Assert.Equal("Apache 2.4.70 is now in use.", vm.Updates.Message);
        Assert.False(vm.Updates.HasRows);
    }

    [Fact]
    public async Task A_version_that_does_not_run_reports_the_switch_back()
    {
        _api.Offers.Add(new UpdateOffer(ComponentKind.Apache, "2.4.69", "2.4.70", null));
        _api.NextResult = ApplyResult.Failure("Apache 2.4.70 did not start: Apache did not answer on port 80 within 15 s. Apache 2.4.69 is running again.");
        var vm = new ApacheViewModel(_api, Paths, Editor, ui: a => a());
        await vm.LoadAsync(Ct);

        await vm.Updates.Rows[0].UpdateCommand.ExecuteAsync(null);

        Assert.Equal(["activate 2.4.70"], _api.Calls);
        Assert.False(vm.Updates.MessageOk);
        Assert.Equal("Apache 2.4.70 did not start: Apache did not answer on port 80 within 15 s. Apache 2.4.69 is running again.", vm.Updates.Message);
    }

    [Fact]
    public async Task A_failed_download_does_not_switch()
    {
        _api.Offers.Add(new UpdateOffer(ComponentKind.Php, "8.5.5", "8.5.12", Release(ComponentKind.Php, "8.5.12")));
        _api.InstallError = new InvalidOperationException("The download's SHA-256 does not match the catalog.");
        var vm = new PhpViewModel(_api, Paths, Editor, ui: a => a());
        await vm.LoadAsync(Ct);

        await vm.Updates.Rows[0].UpdateCommand.ExecuteAsync(null);

        Assert.Empty(_api.Calls);
        Assert.Equal("PHP 8.5.12 was not installed: The download's SHA-256 does not match the catalog.", vm.Updates.Message);
    }

    [Fact]
    public async Task An_agent_that_cannot_check_leaves_the_page_working()
    {
        _api.UpdatesError = new IOException("Pipe is broken.");
        var vm = new ApacheViewModel(_api, Paths, Editor, ui: a => a());

        await vm.LoadAsync(Ct);

        Assert.False(vm.Updates.HasRows);
        Assert.NotEmpty(vm.Modules);
    }
}
