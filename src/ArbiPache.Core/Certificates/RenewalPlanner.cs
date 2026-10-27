using ArbiPache.Core.Sites;

namespace ArbiPache.Core.Certificates;

public enum RenewalAction { None, RenewLetsEncrypt, ReissueLocal, WarnImported }

/// <summary>What one site's certificate needs. DaysLeft is whole days until it expires, 0 when there is none yet.</summary>
public sealed record RenewalDecision(string SiteId, RenewalAction Action, int DaysLeft);

/// <summary>The sites whose certificates a renewal run replaced, and those it could not renew.</summary>
public sealed record RenewalReport(IReadOnlyList<string> Renewed, IReadOnlyList<string> Failed);

/// <summary>Decides what each enabled site's certificate needs today.</summary>
public static class RenewalPlanner
{
    /// <summary>Let's Encrypt and local development certificates renew inside this many days; imported ones warn from it on.</summary>
    public const int WindowDays = 30;

    /// <summary>
    /// One decision per enabled site with a certificate source. A Let's Encrypt or local development site without a
    /// certificate of that source (never issued, or left over from another source) gets one now.
    /// </summary>
    public static IReadOnlyList<RenewalDecision> Plan(
        IReadOnlyList<SiteDefinition> sites, IReadOnlyDictionary<string, SiteCertificate> certs, DateTimeOffset now)
    {
        var plan = new List<RenewalDecision>();
        foreach (var site in sites.Where(s => s.Enabled && s.Kind != SiteKind.Htdocs))
        {
            if (site.Cert is not (CertSource.LetsEncrypt or CertSource.LocalDev or CertSource.Imported)) continue;
            var cert = certs.TryGetValue(site.Id, out var found) && found.Source == site.Cert ? found : null;
            if (cert is null)
            {
                // Nothing to renew from: the person imports the client's certificate.
                if (site.Cert == CertSource.Imported) continue;
                plan.Add(new RenewalDecision(site.Id, site.Cert == CertSource.LetsEncrypt ? RenewalAction.RenewLetsEncrypt : RenewalAction.ReissueLocal, 0));
                continue;
            }

            var daysLeft = (int)Math.Floor((cert.NotAfter - now).TotalDays);
            var action = site.Cert switch
            {
                CertSource.LetsEncrypt when daysLeft < WindowDays => RenewalAction.RenewLetsEncrypt,
                CertSource.LocalDev when daysLeft < WindowDays => RenewalAction.ReissueLocal,
                CertSource.Imported when daysLeft <= WindowDays => RenewalAction.WarnImported,
                _ => RenewalAction.None,
            };
            plan.Add(new RenewalDecision(site.Id, action, daysLeft));
        }

        return plan;
    }
}
