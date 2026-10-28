using System.Text;
using Anvil.Core.Elevation;
using Anvil.Core.Hosts;
using Anvil.Core.IO;
using Anvil.Core.Paths;

namespace Anvil.Elevator.Handlers;

/// <summary>hosts-set: backs the hosts file up to data\backups\hosts, then rewrites Anvil's block.</summary>
internal static class HostsHandler
{
    // Latin-1 maps every byte to one char, so lines Anvil does not own are written back byte for byte.
    private static readonly Encoding Bytes = Encoding.Latin1;

    public static ElevatedResult Run(HostsSetRequest request, AppPaths paths)
    {
        var hostsPath = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
        var current = File.Exists(hostsPath) ? File.ReadAllText(hostsPath, Bytes) : "";
        var next = HostsFile.ApplyBlock(current, request.Domains);
        if (next == current) return new ElevatedResult(true, null);

        var backups = ElevatedFolders.HostsBackups(paths);
        SafeFile.CreateNew(Path.Combine(backups, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt"), current, Bytes);

        AtomicFile.ReplaceText(hostsPath, next, Bytes);
        return new ElevatedResult(true, null);
    }
}
