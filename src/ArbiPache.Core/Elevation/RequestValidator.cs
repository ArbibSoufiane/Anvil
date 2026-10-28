using System.Text.Json;
using System.Text.RegularExpressions;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Settings;
using ArbiPache.Core.Sites;

namespace ArbiPache.Core.Elevation;

/// <summary>
/// What ArbiPache.Elevator.exe accepts. It runs as administrator, so it reads requests only from data\tmp, accepts only
/// known verbs with well-formed payloads, and refuses everything else without changing anything.
/// </summary>
public static partial class RequestValidator
{
    private const int MaxDomains = 1000;

    [GeneratedRegex("^elevate-[0-9a-f]{32}\\.json$")]
    private static partial Regex RequestName();

    /// <summary>A local or domain user account: S-1-5-21 and four numbers.</summary>
    [GeneratedRegex(@"\AS-1-5-21(-[0-9]{1,10}){4}\z")]
    private static partial Regex AccountSid();

    /// <summary>Null when <paramref name="requestPath"/> is a request file directly inside data\tmp.</summary>
    public static string? PathError(string requestPath, AppPaths paths)
    {
        var full = Path.GetFullPath(requestPath);
        var dir = Path.GetDirectoryName(full);
        if (!string.Equals(dir, Path.GetFullPath(paths.Tmp), StringComparison.OrdinalIgnoreCase) || !RequestName().IsMatch(Path.GetFileName(full)))
            return "The request must be an ArbiPache request file in data\\tmp.";
        foreach (var folder in new[] { paths.Data, paths.Tmp })
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
            ServiceInstallRequest s => ServiceInstallError(s, paths),
            ServiceUninstallRequest => null,
            AclGrantRequest a => AclError(a, paths),
            FolderSecureRequest f => f.UserSid is not null && AccountSid().IsMatch(f.UserSid) ? null : "The folder's user must be a user account's SID.",
            _ => "Unknown request.",
        };

    /// <summary>Reads and validates a request file. The path is checked before the file is opened.</summary>
    public static (ElevatedRequest? Request, string? Error) Read(string requestPath, AppPaths paths)
    {
        if (PathError(requestPath, paths) is { } pathError) return (null, pathError);
        ElevatedRequest? request;
        try
        {
            request = ElevatedJson.Deserialize(File.ReadAllText(requestPath));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException)
        {
            return (null, "The request could not be read: " + ex.Message);
        }

        if (request is null) return (null, "The request is empty.");
        return Validate(request, requestPath, paths) is { } error ? (null, error) : (request, null);
    }

    /// <summary>Only ArbiPache's own HTTP and HTTPS ports, as configured in data\arbipache.json, can be opened.</summary>
    private static string? FirewallError(FirewallSetRequest f, AppPaths paths)
    {
        if (f.Ports is null) return "The request lists no ports.";
        if (!Enum.IsDefined(f.Scope)) return "Unknown firewall scope.";
        var settings = new SettingsStore(paths).Load();
        int[] allowed = [settings.HttpPort, settings.HttpsPort];
        foreach (var port in f.Ports)
        {
            if (port is < 1 or > 65535 || !allowed.Contains(port))
                return $"Port {port} is not one of ArbiPache's web ports ({settings.HttpPort}, {settings.HttpsPort}).";
        }

        return null;
    }

    /// <summary>The service may run only the Agent next to the Elevator, and only a user account may control it.</summary>
    private static string? ServiceInstallError(ServiceInstallRequest s, AppPaths paths)
    {
        var agent = Path.Combine(paths.Root, "ArbiPache.Agent.exe");
        if (FullPath(s.AgentExe) is not { } exe || !string.Equals(exe, agent, StringComparison.OrdinalIgnoreCase))
            return "The service can only run the ArbiPache.Agent.exe next to ArbiPache.Elevator.exe.";
        if (!File.Exists(agent)) return "ArbiPache.Agent.exe is missing from the ArbiPache folder.";
        if (s.ControlUserSid is null || !AccountSid().IsMatch(s.ControlUserSid)) return "The control user must be a user account's SID.";
        return null;
    }

    /// <summary>
    /// Only grants from ArbiPache's own plan, recomputed here from data\sites, and never on a system folder or through a
    /// junction or symbolic link: either could hand the service account rights over Windows itself.
    /// </summary>
    private static string? AclError(AclGrantRequest a, AppPaths paths)
    {
        if (a.Grants is null || a.Grants.Count == 0) return "The request lists no folders.";
        var plan = AclPlanner.Plan(paths, new SiteStore(paths).All(), new SettingsStore(paths).Load().HtdocsPath);
        foreach (var g in a.Grants)
        {
            if (g is null || FullPath(g.Path) is not { } full || !Enum.IsDefined(g.Right)) return "The request lists a folder without a path or right.";
            if (!plan.Any(p => string.Equals(p.Path, full, StringComparison.OrdinalIgnoreCase) && p.Right == g.Right))
                return $"{g.Path} is not a folder ArbiPache's service needs.";
            if (IsSystemFolder(full)) return $"{g.Path} is a system folder; ArbiPache does not change its permissions.";
            if (ThroughLink(full)) return $"{g.Path} goes through a link to another folder; ArbiPache does not change its permissions.";
        }

        return null;
    }

    private static string? FullPath(string? path)
    {
        if (path is null || !Path.IsPathFullyQualified(path)) return null;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// A drive root, the Users folder itself, and Windows, Program Files, Program Files (x86) and ProgramData with
    /// everything in them: programs there may run as an administrator.
    /// </summary>
    private static bool IsSystemFolder(string full)
    {
        static string Folder(Environment.SpecialFolder f) => Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(f));
        bool Same(string other) => other.Length > 0 && string.Equals(full, other, StringComparison.OrdinalIgnoreCase);
        bool Inside(string other) => Same(other) || (other.Length > 0 && full.StartsWith(other + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

        if (Same(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? ""))) return true;
        if (Same(Path.GetDirectoryName(Folder(Environment.SpecialFolder.UserProfile)) ?? "")) return true;
        return new[]
        {
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData,
        }.Select(Folder).Any(Inside);
    }

    /// <summary>True when the folder or any folder above it is a junction or symbolic link.</summary>
    private static bool ThroughLink(string full)
    {
        for (var dir = new DirectoryInfo(full); dir is not null; dir = dir.Parent)
        {
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
        }

        return false;
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
