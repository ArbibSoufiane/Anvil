using System.Security.AccessControl;
using System.Security.Principal;
using ArbiPache.Core.Elevation;

namespace ArbiPache.Core.IO;

/// <summary>
/// Limits a folder or file to this Windows account, the Administrators group, the ArbiPache service and the users who
/// control it: the Agent runs as the user or as the service, and each must read what the other wrote. Volumes that keep
/// no permissions (FAT, exFAT) and network shares are left as they are.
/// </summary>
public static class PrivateAcl
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>The settings' control users; the Agent sets them at start.</summary>
    public static IReadOnlyList<string> ControlUsers { get; set; } = [];

    public static void RestrictFolder(string dir)
    {
        if (!KeepsPermissions(dir)) return;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in Holders())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(dir).SetAccessControl(security);
    }

    public static void RestrictFile(string path)
    {
        if (!KeepsPermissions(path)) return;
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in Holders()) security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static IReadOnlyList<SecurityIdentifier> Holders()
    {
        using var me = WindowsIdentity.GetCurrent();
        return Holders(me.User!, ControlUsers);
    }

    internal static IReadOnlyList<SecurityIdentifier> Holders(SecurityIdentifier me, IEnumerable<string> controlUsers) =>
        new[] { me, Administrators, AclPlanner.ServiceSid }.Concat(controlUsers.Select(s => new SecurityIdentifier(s))).Distinct().ToList();

    private static bool KeepsPermissions(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        return new DriveInfo(root).DriveFormat is "NTFS" or "ReFS";
    }
}
