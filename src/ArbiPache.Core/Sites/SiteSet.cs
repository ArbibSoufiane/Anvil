using System.Globalization;
using System.Text.RegularExpressions;

namespace ArbiPache.Core.Sites;

/// <summary>Questions about the whole set of sites: ids, the PHP versions they need, and what htdocs serves.</summary>
public static partial class SiteSet
{
    /// <summary>Folders found in htdocs get this access until the user picks another one.</summary>
    public const AccessLevel DefaultHtdocsAccess = AccessLevel.Network;

    private static readonly IdnMapping Idn = new();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    internal static partial Regex FolderName();

    /// <summary>A domain in the form Apache and certificates use: punycode, lower case. Null when it cannot be converted.</summary>
    public static string? NormalizeHost(string name)
    {
        try
        {
            return Idn.GetAscii(name.Trim().TrimEnd('.')).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static string IdFor(SiteKind kind, string name) => kind switch
    {
        SiteKind.Domain => NormalizeHost(name) ?? name.Trim().ToLowerInvariant(),
        SiteKind.Ip => name.Trim() == "*" ? "ip-all" : "ip-" + name.Trim().ToLowerInvariant().Replace('.', '-').Replace(':', '-'),
        SiteKind.Htdocs => "htdocs-" + name.Trim().ToLowerInvariant(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Every PHP version an enabled site, mount or htdocs entry runs on, the default first, without repeats.</summary>
    public static IReadOnlyCollection<string> RequiredPhpVersions(
        IReadOnlyList<SiteDefinition> sites, IReadOnlyList<HtdocsEntry> htdocs, string? defaultPhp)
    {
        var versions = new List<string>();
        void Add(string? version)
        {
            if (version is not null && !versions.Contains(version, StringComparer.Ordinal)) versions.Add(version);
        }

        Add(defaultPhp);
        foreach (var site in sites.Where(s => s.Enabled))
        {
            if (site.Type == SiteType.Php) Add(site.PhpVersion ?? defaultPhp);
            foreach (var mount in site.Mounts.Where(m => m.Type == SiteType.Php))
                Add(mount.PhpVersion ?? site.PhpVersion ?? defaultPhp);
        }

        foreach (var entry in htdocs) Add(entry.PhpVersion ?? defaultPhp);
        return versions;
    }

    /// <summary>
    /// The folders in htdocs plus the enabled htdocs sites that point elsewhere, sorted by name. A saved htdocs site
    /// supplies the PHP version and access of the folder it names; a disabled one hides it.
    /// </summary>
    public static IReadOnlyList<HtdocsEntry> DiscoverHtdocs(string htdocsDir, IReadOnlyList<SiteDefinition> htdocsSites)
    {
        var saved = htdocsSites.Where(s => s.Kind == SiteKind.Htdocs)
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var entries = new List<HtdocsEntry>();

        var folders = Directory.Exists(htdocsDir) ? Directory.GetDirectories(htdocsDir) : [];
        foreach (var folder in folders)
        {
            var name = Path.GetFileName(folder);
            if (!FolderName().IsMatch(name)) continue;
            if (!saved.Remove(name, out var site))
            {
                entries.Add(new HtdocsEntry(name, folder, false, null, DefaultHtdocsAccess));
                continue;
            }

            if (!site.Enabled) continue;
            var isAlias = !SamePath(site.Root, folder);
            entries.Add(new HtdocsEntry(name, isAlias ? site.Root : folder, isAlias, site.PhpVersion, site.Access));
        }

        foreach (var site in saved.Values.Where(s => s.Enabled))
            entries.Add(new HtdocsEntry(site.Name, site.Root, true, site.PhpVersion, site.Access));

        entries.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Folder, b.Folder));
        return entries;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
