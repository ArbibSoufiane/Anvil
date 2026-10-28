using System.Globalization;
using System.Text.RegularExpressions;

namespace Anvil.Core.Sites;

/// <summary>A name under the network domain, e.g. shop.arbib.pc, for a domain site or an htdocs folder.</summary>
public sealed record NetworkName(string Label, string Host, string? SiteId, string? HtdocsFolder);

/// <summary>A site or folder open to the network that gets no network name, and why.</summary>
public sealed record NetworkNameIssue(string? SiteId, string? HtdocsFolder, string Reason);

public sealed record NetworkNamePlan(IReadOnlyList<NetworkName> Names, IReadOnlyList<NetworkNameIssue> Issues)
{
    public static NetworkNamePlan Empty { get; } = new([], []);
}

/// <summary>
/// The network domain colleagues' PCs ask this PC about, and the name each site and htdocs folder open to the network
/// gets under it: a site's first label (shop.test answers at shop.arbib.pc), a folder's name in lower case.
/// </summary>
public static partial class NetworkNames
{
    private const string Allowed = "the letters a to z, digits and hyphens";

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex Label();

    /// <summary>Trimmed of spaces and dots and lower-cased; null when nothing is left.</summary>
    public static string? NormalizeDomain(string? input)
    {
        var domain = input?.Trim().Trim('.').ToLowerInvariant();
        return string.IsNullOrEmpty(domain) ? null : domain;
    }

    /// <summary>Null when <paramref name="domain"/> can be the network domain.</summary>
    public static string? DomainError(string domain)
    {
        var lower = domain.ToLower(CultureInfo.InvariantCulture);
        if (lower == "localhost" || lower.EndsWith(".localhost", StringComparison.Ordinal))
            return "A network domain cannot be localhost or end in .localhost.";
        if (lower.Length > 253) return "A network domain can be at most 253 characters long.";
        var labels = lower.Split('.');
        if (labels.Length < 2) return "A network domain needs at least two parts, such as arbib.pc.";
        return labels.FirstOrDefault(l => !Label().IsMatch(l)) is { } bad
            ? $"'{bad}' is not allowed in a network domain: use {Allowed}."
            : null;
    }

    /// <summary>
    /// Names for the enabled domain sites and the htdocs folders open to the network. When two want the same name, the
    /// older site wins, a site wins over a folder, and folders go in name order; the others are reported.
    /// </summary>
    public static NetworkNamePlan Plan(string? networkDomain, IReadOnlyList<SiteDefinition> sites, IReadOnlyList<HtdocsEntry> htdocs,
        bool htdocsOnNetwork)
    {
        if (networkDomain is null) return NetworkNamePlan.Empty;

        var names = new List<NetworkName>();
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var issues = new List<NetworkNameIssue>();

        void Claim(string wanted, string display, string? siteId, string? folder)
        {
            var label = wanted.ToLower(CultureInfo.InvariantCulture);
            if (!Label().IsMatch(label))
            {
                issues.Add(new NetworkNameIssue(siteId, folder, $"{display} has no network name: a network name may use only {Allowed}."));
                return;
            }

            var host = $"{label}.{networkDomain}";
            if (owners.TryGetValue(label, out var owner))
            {
                issues.Add(new NetworkNameIssue(siteId, folder, $"{host} already belongs to {owner}."));
                return;
            }

            owners[label] = siteId is null ? $@"htdocs\{folder}" : display;
            names.Add(new NetworkName(label, host, siteId, folder));
        }

        foreach (var site in sites.Where(s => s.Kind == SiteKind.Domain && s.Enabled && s.Access == AccessLevel.Network)
                     .OrderBy(s => s.Created).ThenBy(s => s.Id, StringComparer.Ordinal))
            Claim(site.Name.Split('.')[0], site.Name, site.Id, null);

        if (htdocsOnNetwork)
        {
            foreach (var entry in htdocs.Where(e => e.Access == AccessLevel.Network).OrderBy(e => e.Folder, StringComparer.Ordinal))
                Claim(entry.Folder, entry.Folder, null, entry.Folder);
        }

        return new NetworkNamePlan(names, issues);
    }
}
