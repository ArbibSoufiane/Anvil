using ArbiPache.Core.Certificates;
using ArbiPache.Core.Sites;

namespace ArbiPache.Core.Tests.Certificates;

public sealed class RenewalPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 3, 17, 0, TimeSpan.Zero);

    private static SiteDefinition Site(CertSource cert, bool enabled = true, string name = "shop.example.com") => new()
    {
        Id = name, Kind = SiteKind.Domain, Type = SiteType.Php, Name = name, Root = @"D:\Sites\shop", Cert = cert, Enabled = enabled,
    };

    private static Dictionary<string, SiteCertificate> Certs(SiteDefinition site, CertSource source, TimeSpan left) => new()
    {
        [site.Id] = new SiteCertificate(source, [site.Name], Now + left, "cert.pem", "key.pem", null),
    };

    private static RenewalDecision Decide(SiteDefinition site, CertSource source, TimeSpan left) =>
        Assert.Single(RenewalPlanner.Plan([site], Certs(site, source, left), Now));

    [Fact]
    public void LetsEncrypt_29_days_left_renews()
    {
        var site = Site(CertSource.LetsEncrypt);

        Assert.Equal(new RenewalDecision(site.Id, RenewalAction.RenewLetsEncrypt, 29), Decide(site, CertSource.LetsEncrypt, TimeSpan.FromDays(29.5)));
    }

    [Fact]
    public void LetsEncrypt_31_days_left_none()
    {
        var site = Site(CertSource.LetsEncrypt);

        Assert.Equal(new RenewalDecision(site.Id, RenewalAction.None, 31), Decide(site, CertSource.LetsEncrypt, TimeSpan.FromDays(31)));
    }

    [Fact]
    public void Local_dev_under_30_days_is_reissued()
    {
        var site = Site(CertSource.LocalDev, name: "shop.test");

        Assert.Equal(RenewalAction.ReissueLocal, Decide(site, CertSource.LocalDev, TimeSpan.FromDays(10)).Action);
        Assert.Equal(RenewalAction.None, Decide(site, CertSource.LocalDev, TimeSpan.FromDays(400)).Action);
    }

    [Fact]
    public void Imported_7_days_left_warns()
    {
        var site = Site(CertSource.Imported);

        Assert.Equal(new RenewalDecision(site.Id, RenewalAction.WarnImported, 7), Decide(site, CertSource.Imported, TimeSpan.FromDays(7)));
        Assert.Equal(RenewalAction.WarnImported, Decide(site, CertSource.Imported, TimeSpan.FromDays(30)).Action);
        Assert.Equal(RenewalAction.WarnImported, Decide(site, CertSource.Imported, TimeSpan.FromDays(-2)).Action);
        Assert.Equal(RenewalAction.None, Decide(site, CertSource.Imported, TimeSpan.FromDays(31)).Action);
    }

    [Fact]
    public void Disabled_site_skipped()
    {
        var site = Site(CertSource.LetsEncrypt, enabled: false);

        Assert.Empty(RenewalPlanner.Plan([site], Certs(site, CertSource.LetsEncrypt, TimeSpan.FromDays(3)), Now));
    }

    [Fact]
    public void Missing_letsencrypt_certificate_is_requested()
    {
        var site = Site(CertSource.LetsEncrypt);
        var plain = Site(CertSource.None, name: "plain.example.com");
        // A certificate left over from the site's previous source does not count.
        var leftover = Certs(site, CertSource.Imported, TimeSpan.FromDays(200));

        var plan = RenewalPlanner.Plan([site, plain], leftover, Now);

        Assert.Equal(new RenewalDecision(site.Id, RenewalAction.RenewLetsEncrypt, 0), Assert.Single(plan));
    }
}
