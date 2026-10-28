using Anvil.Core.Database;
using Anvil.Core.Modes;
using Anvil.Core.Paths;
using Anvil.Core.Settings;

namespace Anvil.Core.Components;

/// <summary>A newer patch release of a component in use. <paramref name="Release"/> is null when it is installed already.</summary>
public sealed record UpdateOffer(ComponentKind Kind, string Installed, string Available, ComponentRelease? Release);

/// <summary>
/// Updates stay on a branch: Apache 2.4.x, each PHP 8.x in use, the active database series. A newer branch is a new
/// install the user picks on purpose, never an update.
/// </summary>
public static class ComponentUpdater
{
    private static readonly Comparer<string> Newest = Comparer<string>.Create((a, b) => SemVer.Compare(b, a));

    /// <param name="phpInUse">The PHP versions the default, the sites and their mounts use.</param>
    public static IReadOnlyList<UpdateOffer> Offers(AppSettings s, IReadOnlyCollection<string> phpInUse,
        IReadOnlyList<InstalledComponent> installed, IReadOnlyList<ComponentRelease> available)
    {
        var offers = new List<UpdateOffer>();
        void Offer(ComponentKind kind, string? current)
        {
            if (current is null) return;
            var newest = installed.Where(i => i.Kind == kind).Select(i => i.Version)
                .Concat(available.Where(r => r.Kind == kind).Select(r => r.Version))
                .Where(v => SameBranch(v, current) && SemVer.Compare(v, current) > 0)
                .Order(Newest).FirstOrDefault();
            if (newest is null) return;
            var release = installed.Any(i => i.Kind == kind && Same(i.Version, newest)) ? null
                : available.First(r => r.Kind == kind && Same(r.Version, newest));
            offers.Add(new UpdateOffer(kind, current, newest, release));
        }

        Offer(ComponentKind.Apache, s.ActiveApache);
        // One offer per branch, from the newest patch in use: the update moves every older one along.
        foreach (var branch in phpInUse.GroupBy(Branch, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, Comparer<string>.Create(SemVer.Compare)))
            Offer(ComponentKind.Php, branch.Order(Newest).First());
        if (s.DbEngine is { } engine) Offer(engine == DbEngine.MariaDb ? ComponentKind.MariaDb : ComponentKind.MySql, s.DbVersion);
        return offers;
    }

    /// <summary>"8.5.12" is on branch "8.5".</summary>
    public static string Branch(string version)
    {
        var parts = version.Split('.');
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : version;
    }

    public static bool SameBranch(string a, string b) => string.Equals(Branch(a), Branch(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gives a newly installed PHP patch release the php.ini and conf.d of the newest older patch on its branch, so an
    /// update keeps the user's PHP settings. Only when the new version has no php.ini yet; returns the version copied from.
    /// </summary>
    public static string? CarryPhpConfig(AppPaths paths, string version, IEnumerable<string> installedVersions)
    {
        var target = paths.PhpConfigDir(version);
        if (File.Exists(Path.Combine(target, "php.ini"))) return null;
        var source = installedVersions
            .Where(v => SameBranch(v, version) && SemVer.Compare(v, version) < 0 && File.Exists(Path.Combine(paths.PhpConfigDir(v), "php.ini")))
            .Order(Newest).FirstOrDefault();
        if (source is null) return null;

        var from = paths.PhpConfigDir(source);
        foreach (var file in Directory.EnumerateFiles(from, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            // The mode file is written for each version as its pool starts.
            if (string.Equals(Path.GetFileName(file), PhpModeIniRenderer.FileName, StringComparison.OrdinalIgnoreCase)) continue;
            var copy = Path.Combine(target, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy, overwrite: true);
        }
        return source;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
