using System.Text.RegularExpressions;
using Anvil.Core.Paths;

namespace Anvil.Core.Sites;

public sealed record ValidationError(string Field, string Message);

/// <summary>Checks a site before it is saved. Field names match SiteDefinition properties so the form can mark them.</summary>
public static partial class SiteValidator
{
    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$")]
    private static partial Regex DomainName();

    /// <summary>
    /// Every problem with <paramref name="s"/>, at most one per field. <paramref name="others"/> may include the saved copy of s
    /// itself. With <paramref name="app"/>, a folder that would publish Anvil's own files is a problem too.
    /// </summary>
    public static IReadOnlyList<ValidationError> Validate(SiteDefinition s, IReadOnlyList<SiteDefinition> others, AppPaths? app = null)
    {
        var rest = others.Where(o => o.Id != s.Id).ToList();
        var errors = new List<ValidationError>();
        void Check(string field, string? message)
        {
            if (message is not null) errors.Add(new ValidationError(field, message));
        }

        Check(nameof(SiteDefinition.Name), NameError(s, rest));
        Check(nameof(SiteDefinition.Aliases), AliasError(s, rest));
        Check(nameof(SiteDefinition.Root), RootError(s.Type, s.Root, app));
        Check(nameof(SiteDefinition.Index), s.Index.All(IsDirectiveSafe) ? null : "Index files cannot contain quotes, line breaks, %{ or ${.");
        Check(nameof(SiteDefinition.Access), Enum.IsDefined(s.Access) ? null : "Choose who can open the site.");
        Check(nameof(SiteDefinition.Cert), Enum.IsDefined(s.Cert) ? null : "Choose how the site is served.");
        return errors;
    }

    /// <summary>
    /// False when Apache would read <paramref name="value"/> as more than a value inside a quoted directive argument: a quote
    /// ends the argument, a line break starts a new directive, and <c>%{</c> and <c>${</c> are expanded.
    /// </summary>
    public static bool IsDirectiveSafe(string value) =>
        !value.Any(c => c == '"' || char.IsControl(c))
        && !value.Contains("%{", StringComparison.Ordinal)
        && !value.Contains("${", StringComparison.Ordinal);

    /// <summary>True for a domain already in punycode and lower case, other than localhost.</summary>
    public static bool IsDomain(string host) => host != "localhost" && SiteSet.NormalizeHost(host) == host && DomainName().IsMatch(host);

    private static string? NameError(SiteDefinition s, List<SiteDefinition> rest)
    {
        switch (s.Kind)
        {
            case SiteKind.Domain:
                var host = SiteSet.NormalizeHost(s.Name);
                if (host == "localhost") return "localhost is reserved for the htdocs folders.";
                if (host is null || !DomainName().IsMatch(host)) return "Enter a domain such as shop.example.com.";
                return HostsOf(rest).Contains(host) ? $"{host} is already used by another site." : null;

            default:
                if (!SiteSet.FolderName().IsMatch(s.Name)) return "Use letters, digits, dots, dashes and underscores, starting with a letter or digit.";
                return rest.Any(o => o.Kind == SiteKind.Htdocs && string.Equals(o.Name, s.Name, StringComparison.OrdinalIgnoreCase))
                    ? $"localhost/{s.Name} is already set up."
                    : null;
        }
    }

    private static string? AliasError(SiteDefinition s, List<SiteDefinition> rest)
    {
        if (s.Aliases.Count == 0) return null;
        if (s.Kind != SiteKind.Domain) return "Only domain sites have aliases.";
        var seen = new HashSet<string>(StringComparer.Ordinal) { SiteSet.NormalizeHost(s.Name) ?? s.Name };
        var taken = HostsOf(rest);
        foreach (var alias in s.Aliases)
        {
            var host = SiteSet.NormalizeHost(alias);
            if (host is null || host == "localhost" || !DomainName().IsMatch(host)) return $"{alias} is not a valid domain.";
            if (!seen.Add(host)) return $"{host} is listed twice.";
            if (taken.Contains(host)) return $"{host} is already used by another site.";
        }

        return null;
    }

    private static string? RootError(SiteType type, string root, AppPaths? app)
    {
        if (string.IsNullOrWhiteSpace(root)) return type == SiteType.Proxy ? "Enter the address to forward to." : "Choose the site's folder.";
        if (!IsDirectiveSafe(root))
            return $"The {(type == SiteType.Proxy ? "address" : "folder")} cannot contain quotes, line breaks, %{{ or ${{.";
        if (type != SiteType.Proxy)
            return app is not null && PublishesAppFiles(root, app)
                ? "This folder holds Anvil's own files, so the site would publish its settings and keys. Choose the site's own folder."
                : null;
        return Uri.TryCreate(root, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? null
            : "Enter an address starting with http:// or https://, such as http://localhost:5173.";
    }

    /// <summary>
    /// True when <paramref name="root"/> is Anvil's folder, a folder above it, or one of the folders it keeps its data,
    /// runtimes and catalog in. Folders the person adds beside them, htdocs among them, are theirs.
    /// </summary>
    private static bool PublishesAppFiles(string root, AppPaths app)
    {
        if (!Path.IsPathFullyQualified(root)) return false;
        var full = Path.GetFullPath(root);
        return IsWithin(app.Root, full) || IsWithin(full, app.Data) || IsWithin(full, app.Runtime) || IsWithin(full, app.Catalog);
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or inside it. Windows paths ignore case.</summary>
    private static bool IsWithin(string path, string folder)
    {
        var p = Path.TrimEndingDirectorySeparator(path);
        var f = Path.TrimEndingDirectorySeparator(folder);
        return string.Equals(p, f, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(Path.EndsInDirectorySeparator(f) ? f : f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> HostsOf(IEnumerable<SiteDefinition> sites) =>
        sites.Where(o => o.Kind == SiteKind.Domain)
            .SelectMany(o => o.Aliases.Prepend(o.Name))
            .Select(h => SiteSet.NormalizeHost(h) ?? h)
            .ToHashSet(StringComparer.Ordinal);
}
