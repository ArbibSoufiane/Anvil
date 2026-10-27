using System.Text;
using ArbiPache.Core.Elevation;
using ArbiPache.Core.Hosts;
using ArbiPache.Core.Paths;

namespace ArbiPache.Elevator.Handlers;

/// <summary>hosts-set: backs the hosts file up to data\backups\hosts, then rewrites ArbiPache's block.</summary>
internal static class HostsHandler
{
    // Latin-1 maps every byte to one char, so lines ArbiPache does not own are written back byte for byte.
    private static readonly Encoding Bytes = Encoding.Latin1;

    public static ElevatedResult Run(HostsSetRequest request, AppPaths paths)
    {
        var hostsPath = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
        var current = File.Exists(hostsPath) ? File.ReadAllText(hostsPath, Bytes) : "";
        var next = HostsFile.ApplyBlock(current, request.Domains);
        if (next == current) return new ElevatedResult(true, null);

        var backups = Path.Combine(paths.Backups, "hosts");
        Directory.CreateDirectory(backups);
        if (new DirectoryInfo(paths.Backups).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException($"{paths.Backups} is a link to another folder.");
        SafeFile.CreateNew(Path.Combine(backups, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt"), current, Bytes);

        File.WriteAllText(hostsPath, next, Bytes);
        return new ElevatedResult(true, null);
    }
}
