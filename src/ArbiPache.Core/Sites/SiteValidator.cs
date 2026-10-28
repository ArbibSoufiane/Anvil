using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using ArbiPache.Core.Paths;

namespace ArbiPache.Core.Sites;

public sealed record ValidationError(string Field, string Message);

/// <summary>Checks a site before it is saved. Field names match SiteDefinition properties so the form can mark them.</summary>
public static partial class SiteValidator
{
    private static readonly string[] TestSuffixes = [".test", ".local", ".localhost"];

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$")]
    private static partial Regex DomainName();

    [GeneratedRegex(@"^/[a-z0-9][a-z0-9._-]*(/[a-z0-9][a-z0-9._-]*)*$")]
    private static partial Regex MountPath();

    /// <summary>
    /// Every problem with <paramref name="s"/>, at most one per field. <paramref name="others"/> may include the saved copy of s
    /// itself. With <paramref name="app"/>, a folder that would publish ArbiPache's own files is a problem too.
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
        Check(nameof(SiteDefinition.Mounts), MountError(s, app));
        Check(nameof(SiteDefinition.Access), s.Kind == SiteKind.Htdocs && s.Access == AccessLevel.Everyone
            ? "An htdocs folder cannot be open to everyone. Add it as a domain or IP site to publish it."
            : null);
        Check(nameof(SiteDefinition.AllowedIps), s.Access == AccessLevel.SpecificIps
            ? AddressListError(s.AllowedIps, "Add at least one allowed IP address or range.")
            : null);
        Check(nameof(SiteDefinition.TrustedProxies), s.Cert == CertSource.LoadBalancer
            ? AddressListError(s.TrustedProxies, "Add the load balancer's IP address or range.")
            : null);
        Check(nameof(SiteDefinition.Cert), CertError(s));
        return errors;
    }

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

            case SiteKind.Ip:
                if (s.Name != "*" && !IsAddress(s.Name)) return "Enter an IP address of this machine, or choose all addresses.";
                var ip = IpKey(s.Name);
                return rest.Any(o => o.Kind == SiteKind.Ip && IpKey(o.Name) == ip)
                    ? $"Another site already answers on {(s.Name == "*" ? "all addresses" : s.Name)}."
                    : null;

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
        if (type != SiteType.Proxy)
            return app is not null && PublishesAppFiles(root, app)
                ? "This folder holds ArbiPache's own files, so the site would publish its settings and keys. Choose the site's own folder."
                : null;
        return Uri.TryCreate(root, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? null
            : "Enter an address starting with http:// or https://, such as http://localhost:5173.";
    }

    /// <summary>
    /// True when <paramref name="root"/> is ArbiPache's folder, a folder above it, or one of the folders it keeps its data,
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

    private static string? MountError(SiteDefinition s, AppPaths? app)
    {
        if (s.Mounts.Count == 0) return null;
        if (s.Kind == SiteKind.Htdocs) return "Sub-paths are available on domain and IP sites.";
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mount in s.Mounts)
        {
            if (!MountPath().IsMatch(mount.UrlPath)) return $"{mount.UrlPath} is not a valid path. Use lower case, such as /demo.";
            if (!paths.Add(mount.UrlPath)) return $"{mount.UrlPath} is listed twice.";
            if (RootError(mount.Type, mount.Root, app) is { } rootError) return $"{mount.UrlPath}: {rootError}";
        }

        return null;
    }

    private static string? CertError(SiteDefinition s)
    {
        if (s.Cert != CertSource.LetsEncrypt) return null;
        if (s.Kind != SiteKind.Domain) return "Let's Encrypt issues certificates for public domains only.";
        var hosts = s.Aliases.Prepend(s.Name).Select(h => SiteSet.NormalizeHost(h) ?? h);
        return hosts.FirstOrDefault(h => TestSuffixes.Any(t => h.EndsWith(t, StringComparison.Ordinal))) is { } local
            ? $"Let's Encrypt cannot issue a certificate for {local}. Use a local development certificate."
            : null;
    }

    private static string? AddressListError(IReadOnlyList<string> list, string emptyMessage)
    {
        if (list.Count == 0) return emptyMessage;
        return list.FirstOrDefault(a => !IsAddressOrRange(a)) is { } bad ? $"{bad} is not an IP address or range such as 10.0.0.0/24." : null;
    }

    private static HashSet<string> HostsOf(IEnumerable<SiteDefinition> sites) =>
        sites.Where(o => o.Kind == SiteKind.Domain)
            .SelectMany(o => o.Aliases.Prepend(o.Name))
            .Select(h => SiteSet.NormalizeHost(h) ?? h)
            .ToHashSet(StringComparer.Ordinal);

    private static string IpKey(string name) => IPAddress.TryParse(name, out var a) ? a.ToString() : name.Trim();

    /// <summary>A literal IPv4 address in dotted-quad form, or any IPv6 address.</summary>
    internal static bool IsAddress(string text) =>
        IPAddress.TryParse(text, out var a) && (a.AddressFamily == AddressFamily.InterNetworkV6 || text.Split('.').Length == 4);

    internal static bool IsAddressOrRange(string text)
    {
        var slash = text.IndexOf('/');
        return slash < 0 ? IsAddress(text) : IsAddress(text[..slash]) && IPNetwork.TryParse(text, out _);
    }
}
