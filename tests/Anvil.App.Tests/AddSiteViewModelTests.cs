using Anvil.App.Tests.Support;
using Anvil.App.ViewModels;
using Anvil.Core.Agent;
using Anvil.Core.Php;
using Anvil.Core.Processes;
using Anvil.Core.Settings;
using Anvil.Core.Sites;

namespace Anvil.App.Tests;

public sealed class AddSiteViewModelTests
{
    private const string Htdocs = @"D:\Anvil\htdocs";
    private readonly FakeAgentApi _api = new();
    private readonly FakeFolderPicker _picker = new();
    private readonly List<(SiteDefinition? Site, ApplyResult Result)> _done = [];

    private static readonly IReadOnlyList<PhpVersionInfo> Php =
    [
        new("8.5.5", "vs17", 4, 1000, 9100, ["htdocs"], ProcessState.Running, IsDefault: true, EndOfLife: false),
        new("8.4.23", "vs17", 4, 1000, 0, [], ProcessState.Stopped, IsDefault: false, EndOfLife: false),
    ];

    private AddSiteViewModel Make(SiteDefinition? editing = null) =>
        new(_api, _picker, new SiteFormContext(Php, new AppSettings(), Htdocs, "192.168.10.2"),
            (site, result) =>
            {
                _done.Add((site, result));
                return Task.CompletedTask;
            }, () => { }, editing);

    [Fact]
    public void New_site_defaults_to_my_network_and_offers_two_levels()
    {
        var vm = Make();

        Assert.Equal(AccessLevel.Network, vm.Access);
        Assert.Equal([AccessLevel.ThisPc, AccessLevel.Network], vm.AccessOptions.Select(c => c.Level));
        Assert.Equal([CertSource.None, CertSource.LocalDev], Enum.GetValues<CertSource>());
    }

    [Fact]
    public void Domain_sites_default_to_a_local_certificate_and_htdocs_folders_to_none()
    {
        var vm = Make();
        Assert.Equal(CertSource.LocalDev, vm.Cert);

        vm.Kind = SiteKind.Htdocs;
        Assert.Equal(CertSource.None, vm.Cert);
        Assert.False(vm.ShowCert);

        vm.Kind = SiteKind.Domain;
        Assert.Equal(CertSource.LocalDev, vm.Cert);
    }

    [Fact]
    public void Htdocs_folders_offer_the_same_two_levels()
    {
        var vm = Make();

        vm.Kind = SiteKind.Htdocs;

        Assert.Equal(AccessLevel.Network, vm.Access);
        Assert.Equal(["This PC only", "My network"], vm.AccessOptions.Select(o => o.Label));
    }

    [Fact]
    public void Preview_urls_for_an_htdocs_folder_add_the_network_address_when_open()
    {
        var vm = Make();
        vm.Kind = SiteKind.Htdocs;
        vm.FolderName = "blog";

        Assert.Equal(["http://localhost/blog", "http://192.168.10.2/blog"], vm.PreviewUrls);

        vm.Access = AccessLevel.ThisPc;
        Assert.Equal(["http://localhost/blog"], vm.PreviewUrls);
    }

    [Fact]
    public void Preview_url_for_domain_follows_certificate()
    {
        var vm = Make();
        vm.Name = "shop.test";

        vm.Cert = CertSource.LocalDev;
        Assert.Equal(["https://shop.test"], vm.PreviewUrls);

        vm.Cert = CertSource.None;
        Assert.Equal(["http://shop.test"], vm.PreviewUrls);
    }

    [Fact]
    public async Task Validation_errors_shown_next_to_fields()
    {
        _api.ValidationErrors.Add(new ValidationError("Name", "Enter a domain such as shop.test."));
        _api.ValidationErrors.Add(new ValidationError("Root", "Choose the project folder."));
        var vm = Make();

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Enter a domain such as shop.test.", vm.NameError);
        Assert.Equal("Choose the project folder.", vm.RootError);
        Assert.Null(vm.AccessError);
        Assert.Empty(_api.SavedSites);
        Assert.Empty(_done);
    }

    [Fact]
    public async Task Detected_project_fills_root_and_label()
    {
        _api.Detected = new Detected(SiteType.Php, @"D:\Projects\shop\public", "Laravel", null);
        _picker.Folder = @"D:\Projects\shop";
        var vm = Make();
        vm.Name = "shop.test";

        await vm.BrowseCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(@"D:\Projects\shop", vm.ProjectFolder);
        Assert.Equal(@"Laravel detected. Serving the public\ folder.", vm.DetectedText);
        var saved = Assert.Single(_api.SavedSites);
        Assert.Equal(@"D:\Projects\shop\public", saved.Root);
        Assert.Equal("shop.test", saved.Id);
        Assert.Equal(CertSource.LocalDev, saved.Cert);
        Assert.Equal("shop.test", Assert.Single(_done).Site?.Name);
    }

    [Fact]
    public async Task Copy_into_htdocs_calls_agent()
    {
        _picker.Folder = @"D:\Projects\blog";
        var vm = Make();
        vm.Kind = SiteKind.Htdocs;
        await vm.BrowseCommand.ExecuteAsync(null);
        Assert.Equal("blog", vm.FolderName);
        vm.CopyIntoHtdocs = true;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Contains(@"copy D:\Projects\blog blog", _api.Calls);
        Assert.Empty(_api.SavedSites);
        Assert.Null(Assert.Single(_done).Site);
    }

    [Fact]
    public async Task Kept_where_it_is_saves_an_htdocs_alias()
    {
        _picker.Folder = @"D:\Tools\phpMyAdmin";
        var vm = Make();
        vm.Kind = SiteKind.Htdocs;
        await vm.BrowseCommand.ExecuteAsync(null);
        vm.FolderName = "pma";
        vm.CopyIntoHtdocs = false;
        vm.Access = AccessLevel.ThisPc;

        await vm.SaveCommand.ExecuteAsync(null);

        var saved = Assert.Single(_api.SavedSites);
        Assert.Equal(SiteKind.Htdocs, saved.Kind);
        Assert.Equal("pma", saved.Name);
        Assert.Equal(@"D:\Tools\phpMyAdmin", saved.Root);
        Assert.Equal(AccessLevel.ThisPc, saved.Access);
        Assert.Equal(CertSource.None, saved.Cert);
        Assert.DoesNotContain(_api.Calls, c => c.StartsWith("copy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Editing_keeps_the_id_and_fields_the_form_does_not_show()
    {
        var existing = new SiteDefinition
        {
            Id = "shop.test", Kind = SiteKind.Domain, Type = SiteType.Php, Name = "shop.test", Root = @"D:\shop", PhpVersion = "8.4.23",
            AddToHosts = true, HttpRedirect = false, Cert = CertSource.LocalDev, Created = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
        };
        var vm = Make(existing);
        Assert.Equal("8.4.23", vm.SelectedPhp?.Version);

        vm.Name = "shop2.test";
        await vm.SaveCommand.ExecuteAsync(null);

        var saved = Assert.Single(_api.SavedSites);
        Assert.Equal("shop.test", saved.Id);
        Assert.Equal("shop2.test", saved.Name);
        Assert.True(saved.AddToHosts);
        Assert.False(saved.HttpRedirect);
        Assert.Equal(existing.Created, saved.Created);
        Assert.Equal("8.4.23", saved.PhpVersion);
    }

    [Fact]
    public async Task Rejected_save_shows_apache_output_and_stays_open()
    {
        _api.NextResult = ApplyResult.Failure("The site was not saved.", "AH00526: Syntax error");
        var vm = Make();
        vm.Name = "shop.test";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Contains("AH00526", vm.GeneralError);
        Assert.Empty(_done);
    }
}
