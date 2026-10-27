using System.Text;
using ArbiPache.Core.Elevation;
using ArbiPache.Core.Paths;
using ArbiPache.Elevator.Handlers;

namespace ArbiPache.Elevator;

/// <summary>
/// Runs one request written by the GUI: ArbiPache.Elevator.exe --request &lt;root&gt;\data\tmp\elevate-&lt;guid&gt;.json.
/// The result goes to &lt;request&gt;.result.json. A request outside data\tmp gets no result file at all.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args is not ["--request", var requestPath]) return 2;
        var paths = AppPaths.FromBaseDirectory();
        if (RequestValidator.PathError(requestPath, paths) is not null) return 3;

        var (request, error) = RequestValidator.Read(requestPath, paths);
        ElevatedResult result;
        try
        {
            result = request switch
            {
                null => new ElevatedResult(false, error),
                HostsSetRequest hosts => HostsHandler.Run(hosts, paths),
                FirewallSetRequest firewall => FirewallHandler.Run(firewall),
                ServiceInstallRequest install => ServiceHandler.Install(install, paths),
                ServiceUninstallRequest => ServiceHandler.Uninstall(),
                AclGrantRequest acl => AclHandler.Run(acl),
                _ => new ElevatedResult(false, "Unknown request."),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException
                                       or System.Security.Principal.IdentityNotMappedException or System.ComponentModel.Win32Exception)
        {
            result = new ElevatedResult(false, ex.Message);
        }

        SafeFile.CreateNew(Path.GetFullPath(requestPath) + ".result.json", ElevatedJson.Serialize(result), Encoding.UTF8);
        return result.Ok ? 0 : 1;
    }
}
