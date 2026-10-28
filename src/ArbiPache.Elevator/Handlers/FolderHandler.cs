using System.Security.Principal;
using ArbiPache.Core.Elevation;
using ArbiPache.Core.Paths;

namespace ArbiPache.Elevator.Handlers;

/// <summary>
/// folder-secure: afterwards only administrators, Windows and the requesting user can change the ArbiPache folder, and every
/// other account keeps reading it. A program in the folder with entries of its own keeps them, and the result names who.
/// </summary>
internal static class FolderHandler
{
    public static ElevatedResult Secure(FolderSecureRequest request, AppPaths paths)
    {
        if (FolderPermissions.Secure(paths.Root, new SecurityIdentifier(request.UserSid)) is { } problem) return new ElevatedResult(false, problem);
        return ServiceFolderCheck.Problem(paths.Root, FolderPermissions.Access(paths.Root), request.UserSid) is { } open
            ? new ElevatedResult(false, open)
            : new ElevatedResult(true, null);
    }
}
