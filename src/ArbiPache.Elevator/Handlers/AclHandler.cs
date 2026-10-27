using System.Security.Principal;
using ArbiPache.Core.Elevation;

namespace ArbiPache.Elevator.Handlers;

/// <summary>acl-grant: adds one inherited allow entry per folder for NT SERVICE\ArbiPacheAgent. Nothing is removed.</summary>
internal static class AclHandler
{
    public static ElevatedResult Run(AclGrantRequest request)
    {
        // The account exists once the service is installed.
        var service = (SecurityIdentifier)new NTAccount(AclPlanner.ServiceAccount).Translate(typeof(SecurityIdentifier));
        foreach (var grant in request.Grants)
        {
            if (FolderPermissions.Grant(grant.Path, service, grant.Right) is { } problem) return new ElevatedResult(false, problem);
        }

        return new ElevatedResult(true, null);
    }
}
