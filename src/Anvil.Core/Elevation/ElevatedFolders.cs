using Anvil.Core.Paths;

namespace Anvil.Core.Elevation;

/// <summary>Folders the Elevator writes into under data\.</summary>
public static class ElevatedFolders
{
    /// <summary>
    /// data\backups\hosts, where hosts-set keeps the file it replaces. Each folder on the way is checked before anything
    /// is created in it: the Elevator runs as administrator, and a folder the user turned into a link would send what it
    /// creates anywhere on the disk.
    /// </summary>
    public static string HostsBackups(AppPaths paths)
    {
        var backups = Path.Combine(paths.Backups, "hosts");
        foreach (var folder in (string[])[paths.Data, paths.Backups, backups])
        {
            if (Directory.Exists(folder) && new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException($"{folder} is a link to another folder.");
            Directory.CreateDirectory(folder);
        }
        return backups;
    }
}
