using ArbiPache.Core.Paths;
using ArbiPache.Core.Sites;

namespace ArbiPache.Core.Elevation;

public enum AclRight { Read, ReadExecute, Modify }

/// <summary>One folder the service account gets, with what it inherits down to every file below.</summary>
public sealed record AclGrant(string Path, AclRight Right);

/// <summary>
/// What NT SERVICE\ArbiPacheAgent may use: the app folder read and execute, data\ modify, htdocs and every site folder
/// read, and the folders PHP frameworks write to (Laravel storage and bootstrap\cache, Symfony var, WordPress uploads)
/// modify. A PHP script running under the service can change nothing else.
/// </summary>
public static class AclPlanner
{
    public const string ServiceName = "ArbiPacheAgent";
    public const string ServiceAccount = @"NT SERVICE\ArbiPacheAgent";

    public static IReadOnlyList<AclGrant> Plan(AppPaths paths, IReadOnlyList<SiteDefinition> sites)
    {
        var grants = new List<AclGrant>
        {
            new(paths.Root, AclRight.ReadExecute),
            new(paths.Data, AclRight.Modify),
            new(paths.Htdocs, AclRight.Read),
        };

        void Add(string path, AclRight right)
        {
            if (!grants.Any(have => Covers(have, path, right))) grants.Add(new AclGrant(path, right));
        }

        if (Directory.Exists(paths.Htdocs))
        {
            foreach (var folder in Directory.GetDirectories(paths.Htdocs))
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
