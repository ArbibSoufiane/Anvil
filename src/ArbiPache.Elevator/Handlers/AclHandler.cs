using ArbiPache.Core.Elevation;

namespace ArbiPache.Elevator.Handlers;

/// <summary>acl-grant: adds one inherited allow entry per folder for NT SERVICE\ArbiPacheAgent. Nothing is removed.</summary>
internal static class AclHandler
{
    public static ElevatedResult Run(AclGrantRequest request)
    {
        foreach (var grant in request.Grants)
        {
            if (FolderPermissions.Grant(grant.Path, AclPlanner.ServiceSid, grant.Right) is { } problem) return new ElevatedResult(false, problem);
        }

        return new ElevatedResult(true, null);
    }
}
