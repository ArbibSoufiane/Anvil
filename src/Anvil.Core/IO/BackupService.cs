using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Anvil.Core.Paths;

namespace Anvil.Core.IO;

/// <summary>
/// Copies of the files a change is about to write, in data\backups\&lt;yyyy-MM-dd-HHmmss&gt;\ with a manifest of where each
/// came from and its SHA-256. Restore puts every file back byte for byte, and removes the ones that did not exist.
/// </summary>
public sealed partial class BackupService(AppPaths paths, TimeProvider time)
{
    private const string ManifestName = "manifest.json";

    /// <summary>One backed-up file. Path is relative to the Anvil folder when inside it, so a moved folder restores in place.</summary>
    /// <param name="Sha256">Null when the file did not exist: restoring removes it.</param>
    private sealed record Entry(string Path, string? Sha256, string? Copy);

    private sealed record Manifest(string Reason, DateTimeOffset Created, IReadOnlyList<Entry> Files);

    /// <summary>The folder the next snapshot gets, without creating it.</summary>
    public string NextSnapshot()
    {
        var stamp = time.GetLocalNow().ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture);
        var dir = Path.Combine(paths.Backups, stamp);
        for (var n = 2; Directory.Exists(dir); n++) dir = Path.Combine(paths.Backups, $"{stamp}-{n}");
        return dir;
    }

    public string Snapshot(IEnumerable<string> files, string reason)
    {
        var dir = NextSnapshot();
        var copies = Path.Combine(dir, "files");
        Directory.CreateDirectory(copies);
        var entries = new List<Entry>();
        foreach (var (file, i) in files.Distinct(StringComparer.OrdinalIgnoreCase).Select((f, i) => (f, i)))
        {
            if (!File.Exists(file))
            {
                entries.Add(new Entry(Stored(file), null, null));
                continue;
            }
            var copy = $"{i}-{Path.GetFileName(file)}";
            File.Copy(file, Path.Combine(copies, copy));
            entries.Add(new Entry(Stored(file), Hash(Path.Combine(copies, copy)), $"files/{copy}"));
        }
        JsonFiles.Write(Path.Combine(dir, ManifestName), new Manifest(reason, time.GetUtcNow(), entries));
        return dir;
    }

    /// <summary>Checks every copy against its hash first, then writes them all back; a damaged snapshot changes nothing.</summary>
    public void Restore(string snapshotDir)
    {
        var manifest = JsonFiles.Read<Manifest>(Path.Combine(snapshotDir, ManifestName))
            ?? throw new InvalidDataException($"{snapshotDir} has no backup manifest.");
        foreach (var e in manifest.Files.Where(e => e.Copy is not null))
            if (Hash(Path.Combine(snapshotDir, e.Copy!)) != e.Sha256)
                throw new InvalidDataException($"The backup of {e.Path} in {snapshotDir} was changed after it was made, so nothing was restored.");

        foreach (var e in manifest.Files)
        {
            var target = Resolve(e.Path);
            if (e.Copy is null)
            {
                File.Delete(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temp = target + ".restoring";
            File.Copy(Path.Combine(snapshotDir, e.Copy), temp, overwrite: true);
            File.Move(temp, target, overwrite: true);
        }
    }

    /// <summary>Snapshot folders, newest first. Other folders in data\backups (the database dumps) are not snapshots.</summary>
    public IReadOnlyList<string> List()
    {
        if (!Directory.Exists(paths.Backups)) return [];
        return Directory.GetDirectories(paths.Backups)
            .Select(d => (Dir: d, Match: SnapshotName().Match(Path.GetFileName(d))))
            .Where(x => x.Match.Success && File.Exists(Path.Combine(x.Dir, ManifestName)))
            .OrderByDescending(x => x.Match.Groups["stamp"].Value, StringComparer.Ordinal)
            .ThenByDescending(x => x.Match.Groups["n"].Success ? int.Parse(x.Match.Groups["n"].Value, CultureInfo.InvariantCulture) : 1)
            .Select(x => x.Dir)
            .ToList();
    }

    private string Stored(string file)
    {
        var full = Path.GetFullPath(file);
        var relative = Path.GetRelativePath(paths.Root, full);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? full : relative;
    }

    private string Resolve(string stored) => Path.IsPathRooted(stored) ? stored : Path.Combine(paths.Root, stored);

    private static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    [GeneratedRegex(@"^(?<stamp>\d{4}-\d{2}-\d{2}-\d{6})(-(?<n>\d+))?$")]
    private static partial Regex SnapshotName();
}
