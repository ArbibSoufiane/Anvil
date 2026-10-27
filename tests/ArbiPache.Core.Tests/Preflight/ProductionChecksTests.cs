using ArbiPache.Core.Database;
using ArbiPache.Core.Modes;
using ArbiPache.Core.Preflight;
using ArbiPache.Core.Settings;
using ArbiPache.Core.Sites;

namespace ArbiPache.Core.Tests.Preflight;

public sealed class ProductionChecksTests
{
    private const string PmaRoot = @"D:\www\phpmyadmin";

    private static readonly AppSettings Production = new() { Mode = ServerMode.Production, RunAsService = true, DbEngine = DbEngine.MariaDb };

    private static IReadOnlyDictionary<string, string> Db(params (string Key, string Value)[] values)
    {
        var ini = new Dictionary<string, string>(OptionNames.Comparer);
        foreach (var (key, value) in values) ini[key] = value;
        return ini;
    }

    private static readonly IReadOnlyDictionary<string, string> LocalDb = Db(("bind-address", "127.0.0.1"));

    private static SiteDefinition Site(string name, AccessLevel access = AccessLevel.Everyone, CertSource cert = CertSource.LetsEncrypt,
        string root = @"D:\www\shop") =>
        new() { Id = name, Kind = SiteKind.Domain, Type = SiteType.Php, Name = name, Root = root, Access = access, Cert = cert };

    private static ModeProfile ProfileWith(string key, string value) =>
        ProfileStore.With(ServerMode.Production, new ModeProfileOverrides(new Dictionary<string, string> { [key] = value }, null, null, null, null, null));

    private static IReadOnlyList<PreflightFinding> Run(AppSettings? s = null, bool service = true, ModeProfile? profile = null,
        IReadOnlyList<SiteDefinition>? sites = null, IReadOnlyDictionary<string, string>? db = null, bool noDb = false) =>
        ProductionChecks.Run(s ?? Production, service, profile ?? BuiltInProfiles.Production, sites ?? [Site("shop.example.com")],
            noDb ? null : db ?? LocalDb, root => root == PmaRoot);

    private static IEnumerable<string> Ids(IReadOnlyList<PreflightFinding> findings) => findings.Select(f => f.Id);

    [Fact]
    public void A_correctly_set_up_production_server_has_no_findings()
    {
        Assert.Empty(Run(sites: [Site("shop.example.com"), Site("pma.example.com", AccessLevel.Network, CertSource.None, PmaRoot)]));
    }

    [Fact]
    public void Development_is_not_checked()
    {
        Assert.Empty(Run(Production with { Mode = ServerMode.Development }, service: false, profile: ProfileWith("display_errors", "On"),
            sites: [Site("pma.test", cert: CertSource.None, root: PmaRoot)], db: Db(("bind-address", "0.0.0.0"))));
    }

    [Fact]
    public void Prod_not_service_when_the_agent_runs_from_the_window()
    {
        var finding = Assert.Single(Run(service: false));

        Assert.Equal("prod-not-service", finding.Id);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("sites stop when you sign out", finding.Message);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("*")]
    [InlineData("::")]
    [InlineData("192.168.10.2")]
    [InlineData("127.0.0.1,192.168.10.2")]
    public void Prod_db_exposed_when_bind_address_is_not_loopback(string bind)
    {
        var finding = Assert.Single(Run(db: Db(("bind_address", bind))));

        Assert.Equal("prod-db-exposed", finding.Id);
        Assert.Contains(bind, finding.Message);
    }

    [Fact]
    public void Prod_db_exposed_when_my_ini_has_no_bind_address()
    {
        // MariaDB and MySQL listen on every address when bind-address is missing.
        Assert.Equal(["prod-db-exposed"], Ids(Run(db: Db(("port", "3306")))));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("::1")]
    [InlineData("\"127.0.0.1\"")]
    [InlineData("127.0.0.1, ::1")]
    public void A_loopback_database_is_not_exposed(string bind)
    {
        Assert.Empty(Run(db: Db(("bind-address", bind))));
    }

    [Fact]
    public void A_database_without_networking_or_none_installed_is_not_exposed()
    {
        Assert.Empty(Run(db: Db(("bind-address", "0.0.0.0"), ("skip-networking", ""))));
        Assert.Empty(Run(noDb: true));
    }

    [Fact]
    public void Prod_pma_public_names_each_phpmyadmin_site_open_to_everyone()
    {
        var sites = new[]
        {
            Site("shop.example.com"),
            Site("pma.example.com", root: PmaRoot),
            Site("db.example.com") with { Mounts = [new SiteMount("/phpmyadmin", PmaRoot, SiteType.Php, null)] },
        };

        var finding = Assert.Single(Run(sites: sites));

        Assert.Equal("prod-pma-public", finding.Id);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("pma.example.com", finding.Message);
        Assert.Contains("db.example.com/phpmyadmin", finding.Message);
        Assert.DoesNotContain("shop.example.com", finding.Message);
        Assert.Contains("Who can open it", finding.Fix);
    }

    [Fact]
    public void Phpmyadmin_behind_an_address_rule_a_password_or_switched_off_is_not_public()
    {
        Assert.Empty(Run(sites:
        [
            Site("a.example.com", AccessLevel.SpecificIps, root: PmaRoot),
            Site("b.example.com", AccessLevel.ThisPc, root: PmaRoot),
            Site("c.example.com", root: PmaRoot) with { PasswordProtected = true },
            Site("d.example.com", root: PmaRoot) with { Enabled = false },
        ]));
    }

    [Fact]
    public void Phpmyadmin_is_recognised_by_its_classes_folder_and_sample_config()
    {
        var root = Directory.CreateTempSubdirectory("ap-pma-").FullName;
        try
        {
            Assert.False(ProductionChecks.LooksLikePhpMyAdmin(root));
            Directory.CreateDirectory(Path.Combine(root, "libraries", "classes"));
            Assert.False(ProductionChecks.LooksLikePhpMyAdmin(root));
            File.WriteAllText(Path.Combine(root, "config.sample.inc.php"), "<?php");
            Assert.True(ProductionChecks.LooksLikePhpMyAdmin(root));
            Assert.False(ProductionChecks.LooksLikePhpMyAdmin(Path.Combine(root, "missing")));
            Assert.False(ProductionChecks.LooksLikePhpMyAdmin("http://127.0.0.1:3000"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("On")]
    [InlineData("on")]
    [InlineData("1")]
    [InlineData("stdout")]
    public void Prod_display_errors_when_the_production_profile_turns_it_on(string value)
    {
        var finding = Assert.Single(Run(profile: ProfileWith("display_errors", value)));

        Assert.Equal("prod-display-errors", finding.Id);
        Assert.Contains($"display_errors = {value}", finding.Message);
        Assert.Contains("Dev / Production", finding.Fix);
    }

    [Theory]
    [InlineData("Off")]
    [InlineData("0")]
    [InlineData("stderr")]
    public void Errors_kept_from_visitors_are_not_reported(string value)
    {
        Assert.Empty(Run(profile: ProfileWith("display_errors", value)));
    }

    [Fact]
    public void Prod_no_https_names_sites_open_to_everyone_over_plain_http()
    {
        var sites = new[]
        {
            Site("shop.example.com", cert: CertSource.None),
            Site("blog.example.com", cert: CertSource.LocalDev),
            Site("lb.example.com", cert: CertSource.LoadBalancer),
            Site("given.example.com", cert: CertSource.Imported),
            Site("intranet.example.com", AccessLevel.Network, CertSource.None),
            Site("old.example.com", cert: CertSource.None) with { Enabled = false },
        };

        var finding = Assert.Single(Run(sites: sites));

        Assert.Equal("prod-no-https", finding.Id);
        Assert.Contains("shop.example.com", finding.Message);
        Assert.Contains("blog.example.com", finding.Message);
        Assert.DoesNotContain("lb.example.com", finding.Message);
        Assert.DoesNotContain("given.example.com", finding.Message);
        Assert.DoesNotContain("intranet.example.com", finding.Message);
        Assert.DoesNotContain("old.example.com", finding.Message);
    }
}
