using Anvil.App.Tests.Support;
using Anvil.App.ViewModels;
using Anvil.Core.Paths;
using Anvil.Core.Sites;

namespace Anvil.App.Tests;

public sealed class CertificateUiTests
{
    private readonly FakeAgentApi _api = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SiteDefinition Site(string name, CertSource cert) =>
        new() { Id = name, Kind = SiteKind.Domain, Type = SiteType.Php, Name = name, Root = $@"D:\Sites\{name}", Cert = cert };

    [Fact]
    public async Task Certificate_column_names_local_certificates_and_http_only()
    {
        _api.Sites.Add(Site("shop.test", CertSource.LocalDev));
        _api.Sites.Add(Site("plain.test", CertSource.None));
        var vm = new SitesViewModel(_api, new FakeElevator(), new FakeCa(), new FakeFolderPicker(), new AppPaths(@"D:\Anvil"),
            () => [], new FakeTextClipboard(), ui: a => a());

        await vm.LoadAsync(Ct);

        string Text(string name) => vm.Sites.Single(r => r.Name == name).Certificate;
        Assert.Equal("Local development", Text("shop.test"));
        Assert.Equal("HTTP only", Text("plain.test"));
    }

    [Fact]
    public async Task Reissue_asks_the_agent_for_new_local_certificates()
    {
        var vm = new SettingsViewModel(_api, new FakeElevator(), new FakeRunKey(), new FakeCa(), new FakeFolderPicker(), new AppPaths(@"D:\Anvil"), () => [],
            new FakeTextClipboard());
        await vm.LoadAsync(Ct);

        await vm.ReissueCommand.ExecuteAsync(null);

        Assert.Contains("reissue", _api.Calls);
        Assert.Equal("Local development certificates issued again.", vm.Message);
    }
}
