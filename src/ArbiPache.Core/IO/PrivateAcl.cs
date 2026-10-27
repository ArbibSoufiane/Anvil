using System.Security.AccessControl;
using System.Security.Principal;

namespace ArbiPache.Core.IO;

/// <summary>
/// Limits a folder or file to this Windows account and the Administrators group. Volumes that keep no permissions
/// (FAT, exFAT) and network shares are left as they are.
/// </summary>
internal static class PrivateAcl
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

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

    private static SecurityIdentifier[] Holders()
    {
        using var me = WindowsIdentity.GetCurrent();
        return [me.User!, Administrators];
    }

    private static bool KeepsPermissions(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        return new DriveInfo(root).DriveFormat is "NTFS" or "ReFS";
    }
}
