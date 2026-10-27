using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArbiPache.Core.Agent;

namespace ArbiPache.Core.Sites;

/// <summary>What an import from ArbiPache 3 did. Skipped entries read "&lt;domain&gt;: &lt;reason&gt;".</summary>
public sealed record ImportReport(IReadOnlyList<string> Imported, IReadOnlyList<string> Skipped, IReadOnlyList<string> MissingPhpVersions)
{
    /// <summary>Set by the Agent: what applying the imported sites did, with the hosts list to hand to the Elevator.</summary>
    public ApplyResult? Result { get; init; }
}

/// <summary>
/// Reads the sites ArbiPache 3 kept in &lt;legacy home&gt;\sites\*.json. Every imported site is a domain site that keeps its
/// hosts entry; HTTPS becomes a local development certificate. A site whose PHP version is not installed comes in
/// switched off, so it can be turned on once that version is installed.
/// </summary>
public static partial class LegacyImporter
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>%ARBIPACHE_HOME%, else %USERPROFILE%\.arbipache: where ArbiPache 3 kept its data.</summary>
    public static string DefaultHome() =>
        Environment.GetEnvironmentVariable("ARBIPACHE_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".arbipache");

    public static ImportReport Import(string legacyHome, IReadOnlyList<SiteDefinition> existing, IReadOnlyCollection<string> installedPhp,
        out IReadOnlyList<SiteDefinition> sites)
    {
        var imported = new List<SiteDefinition>();
        var skipped = new List<string>();
        var missing = new List<string>();
        sites = imported;
        var folder = Path.Combine(legacyHome, "sites");
        if (!Directory.Exists(folder)) return new ImportReport([], [], []);

        var taken = existing.Where(s => s.Kind == SiteKind.Domain)
            .SelectMany(s => s.Aliases.Prepend(s.Name))
            .Select(n => SiteSet.NormalizeHost(n) ?? n)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(folder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            LegacySite? legacy;
            try
            {
                legacy = JsonSerializer.Deserialize<LegacySite>(File.ReadAllText(file), Json);
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                legacy = null;
            }

            if (legacy?.Domain is not { Length: > 0 } domain)
            {
                skipped.Add($"{Path.GetFileName(file)}: unreadable");
                continue;
            }

            if (ParseType(legacy.Type) is not { } type)
            {
                skipped.Add($"{domain}: unknown site type {legacy.Type}");
                continue;
            }

            var phpVersion = type == SiteType.Php ? ParsePhpVersion(legacy.PhpVersion) : null;
            var phpMissing = phpVersion is not null && !installedPhp.Contains(phpVersion);
            var name = SiteSet.NormalizeHost(domain) ?? domain;
            var site = new SiteDefinition
            {
                Id = SiteSet.IdFor(SiteKind.Domain, name),
                Kind = SiteKind.Domain,
                Type = type,
                Name = name,
                Root = (type == SiteType.Proxy ? legacy.ProxyTarget : legacy.Root) ?? "",
                PhpVersion = phpVersion,
                Aliases = legacy.Aliases ?? [],
                Index = legacy.Index is { Count: > 0 } index ? index : ["index.php", "index.html"],
                Cert = legacy.Https ? CertSource.LocalDev : CertSource.None,
                HttpRedirect = legacy.HttpRedirect,
                AddToHosts = true,
                Enabled = legacy.Enabled && !phpMissing,
                Created = DateTimeOffset.TryParse(legacy.Created, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var created)
                    ? created
                    : default,
            };

            var names = site.Aliases.Prepend(site.Name).Select(n => SiteSet.NormalizeHost(n) ?? n).ToList();
            if (names.Any(taken.Contains))
            {
                skipped.Add($"{domain}: already exists");
                continue;
            }

            if (SiteValidator.Validate(site, [.. existing, .. imported]) is [var error, ..])
            {
                skipped.Add($"{domain}: {error.Message}");
                continue;
            }

            imported.Add(site);
            taken.UnionWith(names);
            if (phpMissing && !missing.Contains(phpVersion!)) missing.Add(phpVersion!);
        }

        return new ImportReport(imported.Select(s => s.Name).ToList(), skipped, missing);
    }

    private static SiteType? ParseType(string? type) => type switch
    {
        "php" => SiteType.Php,
        "static" => SiteType.Static,
        "proxy" => SiteType.Proxy,
        _ => null,
    };

    // ArbiPache 3 named PHP versions after their download folder: php-8.4.23-Win32-vs17-x64.
    private static string? ParsePhpVersion(string? name) =>
        name is not null && PhpFolderName().Match(name) is { Success: true } m ? m.Groups["version"].Value : null;

    [GeneratedRegex(@"^php-(?<version>\d+\.\d+\.\d+)-Win32-v[cs]\d+-(x64|x86)$")]
    private static partial Regex PhpFolderName();

    private sealed record LegacySite(
        string? Domain,
        string? Type,
        string? Root,
        string? PhpVersion,
        List<string>? Index,
        List<string>? Aliases,
        string? ProxyTarget,
        string? Created,
        bool Https = true,
        bool HttpRedirect = true,
        bool Enabled = true);
}
