using System.IO.Compression;

namespace ArbiPache.Core.Components;

public static class ZipExtractor
{
    /// <summary>
    /// Extracts <paramref name="zipPath"/> into <paramref name="destDir"/>. With <paramref name="stripRoot"/>,
    /// only entries under that folder are kept, without it. Every entry is checked before anything is written:
    /// one that would land outside <paramref name="destDir"/> aborts the whole extraction.
    /// </summary>
    public static void Extract(string zipPath, string destDir, string? stripRoot = null)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destDir)) + Path.DirectorySeparatorChar;
        var prefix = stripRoot is null ? null : stripRoot.Replace('\\', '/').Trim('/') + "/";

        using var zip = ZipFile.OpenRead(zipPath);
        var plan = new List<(ZipArchiveEntry Entry, string Target, bool IsDirectory)>();
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (prefix is not null)
            {
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                name = name[prefix.Length..];
                if (name.Length == 0) continue;
            }
            var target = Path.GetFullPath(Path.Combine(root, name));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Archive entry '{entry.FullName}' points outside the target folder.");
            plan.Add((entry, target, name.EndsWith('/')));
        }

        Directory.CreateDirectory(root);
        foreach (var (entry, target, isDirectory) in plan)
        {
            if (isDirectory)
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }
}
