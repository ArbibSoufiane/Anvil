using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Anvil.Core.Paths;
using Anvil.Core.Sites;

namespace Anvil.Core.Elevation;

public enum AclRight { Read, ReadExecute, Modify }

/// <summary>One folder the service account gets, with what it inherits down to every file below.</summary>
public sealed record AclGrant(string Path, AclRight Right);

/// <summary>
/// What NT SERVICE\AnvilAgent may use: the app folder read and execute, data\ and runtime\ (component installs)
/// modify, htdocs and every site folder read, and the folders PHP frameworks write to (Laravel storage and
/// bootstrap\cache, Symfony var, WordPress uploads) modify. PHP runs as the service too, so a script can also change
/// data\ and runtime\ and read the keys and secrets there; outside these folders it can change nothing.
/// </summary>
public static class AclPlanner
{
    public const string ServiceName = "AnvilAgent";
    public const string ServiceAccount = @"NT SERVICE\AnvilAgent";

    /// <summary>The service account's SID. Windows derives it from the name, so it is known before the service exists.</summary>
    public static SecurityIdentifier ServiceSid { get; } = ServiceSidOf(ServiceName);

    /// <summary>S-1-5-80 followed by the SHA-1 of the upper-case service name (UTF-16), as five little-endian numbers.</summary>
    public static SecurityIdentifier ServiceSidOf(string serviceName)
    {
        var hash = SHA1.HashData(Encoding.Unicode.GetBytes(serviceName.ToUpperInvariant()));
        var parts = Enumerable.Range(0, 5).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(i * 4)));
        return new SecurityIdentifier($"S-1-5-80-{string.Join('-', parts)}");
    }

    /// <param name="htdocs">The htdocs folder from the settings; null means the one in the app folder.</param>
    public static IReadOnlyList<AclGrant> Plan(AppPaths paths, IReadOnlyList<SiteDefinition> sites, string? htdocs = null)
    {
        htdocs = htdocs is not null && Path.IsPathFullyQualified(htdocs) ? Normal(htdocs) : paths.Htdocs;
        var grants = new List<AclGrant>
        {
            new(paths.Root, AclRight.ReadExecute),
            new(paths.Data, AclRight.Modify),
            new(paths.Runtime, AclRight.Modify),
            new(htdocs, AclRight.Read),
        };

        void Add(string path, AclRight right)
        {
            if (!grants.Any(have => Covers(have, path, right))) grants.Add(new AclGrant(path, right));
        }

        if (Directory.Exists(htdocs))
        {
            foreach (var folder in Directory.GetDirectories(htdocs))
            foreach (var write in WriteFolders(folder))
                Add(write, AclRight.Modify);
        }

        var roots = sites.Where(s => s.Type != SiteType.Proxy).Select(s => s.Root)
            .Concat(sites.SelectMany(s => s.Mounts).Where(m => m.Type != SiteType.Proxy).Select(m => m.Root));
        foreach (var root in roots.Where(Path.IsPathFullyQualified))
        {
            var project = ProjectOf(Normal(root));
            Add(project, AclRight.Read);
            foreach (var write in WriteFolders(project)) Add(write, AclRight.Modify);
        }
        return grants;
    }

    /// <summary>A site served from public\ (Laravel, Symfony, Composer projects) reads its whole project folder.</summary>
    private static string ProjectOf(string root)
    {
        if (!string.Equals(Path.GetFileName(root), "public", StringComparison.OrdinalIgnoreCase)) return root;
        var parent = Path.GetDirectoryName(root);
        return parent is not null && new[] { "artisan", @"bin\console", "composer.json" }.Any(f => File.Exists(Path.Combine(parent, f)))
            ? parent
            : root;
    }

    /// <summary>The existing folders the project's framework writes to.</summary>
    private static IEnumerable<string> WriteFolders(string project)
    {
        bool Has(string file) => File.Exists(Path.Combine(project, file));
        var folders = new List<string>();
        if (Has("artisan")) folders.AddRange(["storage", @"bootstrap\cache"]);
        if (Has(@"bin\console")) folders.Add("var");
        if (Has("wp-config.php") || Has("wp-config-sample.php")) folders.Add(@"wp-content\uploads");
        return folders.Select(f => Path.Combine(project, f)).Where(Directory.Exists);
    }

    private static bool Covers(AclGrant have, string path, AclRight right) =>
        have.Right >= right
        && (string.Equals(have.Path, path, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(have.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private static string Normal(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
