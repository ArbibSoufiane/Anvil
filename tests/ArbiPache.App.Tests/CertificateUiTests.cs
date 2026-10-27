using ArbiPache.App.Tests.Support;
using ArbiPache.App.ViewModels;
using ArbiPache.Core.Agent;
using ArbiPache.Core.Certificates;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Php;
using ArbiPache.Core.Processes;
using ArbiPache.Core.Settings;
using ArbiPache.Core.Sites;

namespace ArbiPache.App.Tests;

public sealed class CertificateUiTests
{
    private const string Htdocs = @"D:\ArbiPache\htdocs";
    private readonly FakeAgentApi _api = new();
    private readonly FakeFolderPicker _picker = new();
    private readonly List<(SiteDefinition? Site, ApplyResult Result)> _done = [];
    private int _cancelled;

    private static readonly IReadOnlyList<PhpVersionInfo> Php =
    [
        new("8.5.5", "vs17", 4, 1000, 9100, ["htdocs"], ProcessState.Running, IsDefault: true, EndOfLife: false),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AddSiteViewModel Make(SiteDefinition? editing = null, AppSettings? settings = null,
        IReadOnlyDictionary<string, SiteCertificate>? certificates = null) =>
        new(_api, _picker, new SiteFormContext(Php, ["192.168.10.2"], settings ?? new AppSettings { Mode = ServerMode.Production }, Htdocs, "192.168.10.2")
            {
                Certificates = certificates ?? new Dictionary<string, SiteCertificate>(),
            },
            (site, result) =>
            {
                _done.Add((site, result));
                return Task.CompletedTask;
            }, () => _cancelled++, editing);

    private static SiteDefinition Site(string name, CertSource cert) =>
        new() { Id = name, Kind = SiteKind.Domain, Type = SiteType.Php, Name = name, Root = $@"D:\Sites\{name}", Cert = cert };

    private static SiteCertificate Cert(string name, CertSource source, double daysLeft) =>
        new(source, [name], DateTimeOffset.UtcNow.AddDays(daysLeft), $@"D:\ArbiPache\data\certs\{name}\cert.pem",
            $@"D:\ArbiPache\data\certs\{name}\key.pem", null);

    private AddSiteViewModel ImportedSite()
    {
        var vm = Make();
        vm.Name = "portal.clientco.com";
        vm.Cert = CertSource.Imported;
        return vm;
    }

    [Fact]
    public async Task Import_error_code_maps_to_plain_message()
    {
        _api.CertificateError = new CertificateImportException("key-mismatch", "raw text the GUI does not show");
        var vm = ImportedSite();
        vm.CertFile = @"D:\inbox\portal.crt";
        vm.KeyFile = @"D:\inbox\portal.key";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("The private key does not belong to this certificate.", vm.CertError);
        Assert.Equal(["save site portal.clientco.com", @"import cert portal.clientco.com D:\inbox\portal.crt"], _api.Calls);
        Assert.Empty(_done);
        Assert.True(vm.IsEditing);
        Assert.Equal("Close", vm.CancelLabel);
    }

    [Fact]
    public async Task Closing_after_a_refused_certificate_reports_the_saved_site()
    {
        _api.CertificateError = new CertificateImportException("bad-password", "The password does not open this .pfx file.");
        var vm = ImportedSite();
        vm.CertFile = @"D:\inbox\portal.pfx";
        vm.CertPassword = "guess";
        await vm.SaveCommand.ExecuteAsync(null);

        await vm.CancelCommand.ExecuteAsync(null);

        Assert.Equal("The password does not open this .pfx file.", vm.CertError);
        Assert.Equal("portal.clientco.com", Assert.Single(_done).Site?.Id);
        Assert.Equal(0, _cancelled);
    }

    [Fact]
    public async Task Pfx_file_asks_for_a_password_and_pem_for_a_key()
    {
        var vm = ImportedSite();
        _picker.File = @"D:\inbox\portal.pfx";
        await vm.ChooseCertFileCommand.ExecuteAsync(null);
        Assert.True(vm.IsPfx);
        Assert.False(vm.ShowKeyFiles);

        _picker.File = @"D:\inbox\portal.crt";
        await vm.ChooseCertFileCommand.ExecuteAsync(null);

        Assert.False(vm.IsPfx);
        Assert.True(vm.ShowKeyFiles);
        Assert.Equal(@"D:\inbox\portal.crt", vm.CertFile);
    }

    [Fact]
    public async Task New_imported_site_needs_the_files()
    {
        var vm = ImportedSite();

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Choose the certificate file the client sent.", vm.CertError);

        vm.CertFile = @"D:\inbox\portal.crt";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Choose the private key file that goes with the certificate.", vm.CertError);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Editing_keeps_the_current_certificate_without_new_files()
    {
        var site = Site("portal.clientco.com", CertSource.Imported);
        var vm = Make(site, certificates: new Dictionary<string, SiteCertificate> { [site.Id] = Cert(site.Id, CertSource.Imported, 200.5) });

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.StartsWith("Current certificate: portal.clientco.com, expires ", vm.CurrentCertificateText);
        Assert.Equal(["save site portal.clientco.com"], _api.Calls);
        Assert.Single(_done);
    }

    [Fact]
    public async Task LetsEncrypt_shows_dns_result_before_ordering()
    {
        _api.Resolved.Add("203.0.113.5");
        var vm = Make(settings: new AppSettings { Mode = ServerMode.Production, AcmeStaging = true });
        vm.Name = "shop.example.com";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(CertSource.LetsEncrypt, _api.SavedSites.Single().Site.Cert);
        Assert.Equal(["save site shop.example.com", "resolve shop.example.com", "issue shop.example.com staging"], _api.Calls);
        Assert.Equal("shop.example.com points to 203.0.113.5.", vm.DnsText);
        Assert.Single(_done);
    }

    [Fact]
    public async Task Unresolved_domain_waits_for_dns_instead_of_ordering()
    {
        var vm = Make();
        vm.Name = "shop.example.com";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["save site shop.example.com", "resolve shop.example.com"], _api.Calls);
        Assert.Contains("does not point to any address yet", vm.DnsText);
        Assert.Empty(_done);
        Assert.Equal("Close", vm.CancelLabel);
    }

    [Fact]
    public async Task Existing_letsencrypt_certificate_is_not_ordered_again()
    {
        var site = Site("shop.example.com", CertSource.LetsEncrypt);
        var vm = Make(site, certificates: new Dictionary<string, SiteCertificate> { [site.Id] = Cert(site.Id, CertSource.LetsEncrypt, 60) });

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["save site shop.example.com"], _api.Calls);
        Assert.Single(_done);
    }

    [Fact]
    public async Task New_alias_orders_a_certificate_that_covers_it()
    {
        _api.Resolved.Add("203.0.113.5");
        var site = Site("shop.example.com", CertSource.LetsEncrypt);
        var vm = Make(site, certificates: new Dictionary<string, SiteCertificate> { [site.Id] = Cert(site.Id, CertSource.LetsEncrypt, 60) });
        vm.Aliases = "www.shop.example.com";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["save site shop.example.com", "resolve shop.example.com", "resolve www.shop.example.com", "issue shop.example.com"], _api.Calls);
    }

    [Fact]
    public async Task Switching_to_letsencrypt_orders_even_with_another_certificate_on_disk()
    {
        _api.Resolved.Add("203.0.113.5");
        var site = Site("shop.example.com", CertSource.LetsEncrypt);
        var vm = Make(site, certificates: new Dictionary<string, SiteCertificate> { [site.Id] = Cert(site.Id, CertSource.LocalDev, 700) });

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(vm.CurrentCertificateText);
        Assert.Equal(["save site shop.example.com", "resolve shop.example.com", "issue shop.example.com"], _api.Calls);
    }

    [Fact]
    public async Task Certificate_column_shows_days_left()
    {
        _api.Sites.Add(Site("shop.example.com", CertSource.LetsEncrypt));
        _api.Sites.Add(Site("portal.clientco.com", CertSource.Imported));
        _api.Sites.Add(Site("old.clientco.com", CertSource.Imported));
        _api.Sites.Add(Site("plain.example.com", CertSource.None));
        _api.Certificates["shop.example.com"] = Cert("shop.example.com", CertSource.LetsEncrypt, 61.5);
        _api.Certificates["old.clientco.com"] = Cert("old.clientco.com", CertSource.Imported, -3);
        var vm = new SitesViewModel(_api, new FakeElevator(), new FakeCa(), _picker, new AppPaths(@"D:\ArbiPache"), () => ["192.168.10.2"],
            () => "192.168.10.2", ui: a => a());

        await vm.LoadAsync(Ct);

        string Text(string name) => vm.Sites.Single(r => r.Name == name).Certificate;
        Assert.Equal("Let's Encrypt · 61 days left", Text("shop.example.com"));
        Assert.Equal("Client certificate · missing", Text("portal.clientco.com"));
        Assert.Equal("Client certificate · expired", Text("old.clientco.com"));
        Assert.Equal("HTTP only", Text("plain.example.com"));
    }

    [Fact]
    public async Task Acme_settings_are_saved()
    {
        var vm = new SettingsViewModel(_api, new FakeElevator(), new FakeRunKey(), new FakeCa(), _picker, new AppPaths(@"D:\ArbiPache"), () => null);
        await vm.LoadAsync(Ct);

        vm.AcmeEmail = "not-an-email";
        await vm.ApplyCertificatesCommand.ExecuteAsync(null);
        Assert.Equal("Enter an email address such as ops@example.com, or leave it empty.", vm.AcmeEmailError);
        Assert.Null(_api.Saved);

        vm.AcmeEmail = " ops@example.com ";
        vm.AcmeStaging = true;
        await vm.ApplyCertificatesCommand.ExecuteAsync(null);

        Assert.Null(vm.AcmeEmailError);
        Assert.Equal("ops@example.com", _api.Saved?.AcmeEmail);
        Assert.True(_api.Saved?.AcmeStaging);
    }
}
