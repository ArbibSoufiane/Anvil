using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Anvil.Core.Paths;
using Anvil.Core.Settings;
using Anvil.Core.Sites;

namespace Anvil.Core.Elevation;

/// <summary>
/// What Anvil.Elevator.exe accepts. It runs as administrator, so it reads requests only from data\tmp\elevation, accepts only
/// known verbs with well-formed payloads, and refuses everything else without changing anything.
/// </summary>
public static partial class RequestValidator
{
    private const int MaxDomains = 1000;

    [GeneratedRegex("^elevate-[0-9a-f]{32}\\.json$")]
    private static partial Regex RequestName();

    [GeneratedRegex(@"^[0-9]+\.[0-9]+\.[0-9]+$")]
    private static partial Regex ApacheVersion();

    /// <summary>Null when <paramref name="requestPath"/> is a request file directly inside data\tmp\elevation.</summary>
    public static string? PathError(string requestPath, AppPaths paths)
    {
        var full = Path.GetFullPath(requestPath);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(paths.ElevationRequests), StringComparison.OrdinalIgnoreCase))
            return "Requests must sit in data\\tmp\\elevation.";
        if (!RequestName().IsMatch(Path.GetFileName(full))) return "The request must be an Anvil request file.";
        foreach (var folder in new[] { paths.Data, paths.Tmp, paths.ElevationRequests })
        {
            if (Directory.Exists(folder) && new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint))
                return $"{folder} is a link to another folder; the request is refused.";
        }

        return null;
    }

    /// <summary>Null when the request may run.</summary>
    public static string? Validate(ElevatedRequest r, string requestPath, AppPaths paths) =>
        PathError(requestPath, paths) ?? r switch
        {
            HostsSetRequest h => HostsError(h),
            FirewallSetRequest f => FirewallError(f, paths),
            _ => "Unknown request.",
        };

    /// <summary>Reads and validates a request file. The path is checked before the file is opened.</summary>
    public static (ElevatedRequest? Request, string? Error) Read(string requestPath, AppPaths paths)
    {
        if (PathError(requestPath, paths) is { } pathError) return (null, pathError);
        ElevatedRequest? request;
        try
        {
            var json = File.ReadAllText(requestPath);
            // A verb from an older build (the service and folder verbs are gone) is refused as unknown, not as unreadable.
            if (JsonNode.Parse(json) is JsonObject node && node["verb"] is JsonValue verb && verb.TryGetValue<string>(out var name)
                && !ElevatedRequest.Verbs.Contains(name))
                return (null, "Unknown request.");
            request = ElevatedJson.Deserialize(json);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or InvalidOperationException)
        {
            return (null, "The request could not be read: " + ex.Message);
        }

        if (request is null) return (null, "The request is empty.");
        return Validate(request, requestPath, paths) is { } error ? (null, error) : (request, null);
    }

    /// <summary>
    /// Only an installed Apache and only Anvil's own HTTP and HTTPS ports, as configured in data\anvil.json. The settings are
    /// read without moving a damaged file: this runs as administrator.
    /// </summary>
    private static string? FirewallError(FirewallSetRequest f, AppPaths paths)
    {
        if (f.Ports is null) return "The request lists no ports.";
        if (f.ApacheVersion is { } version)
        {
            if (!ApacheVersion().IsMatch(version)) return $"'{version}' is not an Apache version.";
            if (!File.Exists(FirewallRulePlanner.HttpdPath(paths, version))) return $"Apache {version} is not installed.";
        }

        if (new SettingsStore(paths).TryRead() is not { } settings) return "Anvil's settings file cannot be read.";
        int[] allowed = [settings.HttpPort, settings.HttpsPort];
        foreach (var port in f.Ports)
        {
            if (port is < 1 or > 65535 || !allowed.Contains(port))
                return $"Port {port} is not one of Anvil's web ports ({settings.HttpPort}, {settings.HttpsPort}).";
        }

        return null;
    }

    private static string? HostsError(HostsSetRequest h)
    {
        if (h.Domains is null) return "The request lists no domains.";
        if (h.Domains.Count > MaxDomains) return $"At most {MaxDomains} domains can be added to the hosts file.";
        foreach (var domain in h.Domains)
        {
            if (domain is null || !SiteValidator.IsDomain(domain)) return $"'{domain}' is not a lower-case domain name.";
        }

        return null;
    }
}
