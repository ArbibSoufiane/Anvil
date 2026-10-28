using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace Anvil.Core.Elevation;

/// <summary>Folder permissions for the elevator: who may write the app folder, and grants for the service account.</summary>
public static class FolderPermissions
{
    private const FileSystemRights GenericAll = (FileSystemRights)0x10000000;
    private const FileSystemRights GenericWrite = (FileSystemRights)0x40000000;

    private const FileSystemRights WriteRights = FileSystemRights.WriteData | FileSystemRights.AppendData
        | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | GenericAll | GenericWrite;

    /// <summary>The allow entries on the folder (they reach everything below it) and on the programs directly in it.</summary>
    public static IReadOnlyList<FolderAccess> Access(string folder)
    {
        var rules = Rules(new DirectoryInfo(folder).GetAccessControl()).ToList();
        foreach (var program in Directory.EnumerateFiles(folder, "*.exe").Concat(Directory.EnumerateFiles(folder, "*.dll")))
            rules.AddRange(Rules(new FileInfo(program).GetAccessControl()));

        return rules.Where(r => r.AccessControlType == AccessControlType.Allow).Select(r =>
        {
            var sid = (SecurityIdentifier)r.IdentityReference;
            return new FolderAccess(sid.Value, NameOf(sid), (r.FileSystemRights & WriteRights) != 0);
        }).ToList();
    }

    /// <summary>
    /// Adds an allow entry for <paramref name="account"/> that everything in the folder inherits. Windows stops inheritance at
    /// links inside the folder. The folder is opened itself, never a link's target, and kept open so it cannot be swapped for a
    /// link while its permissions change. Null on success, otherwise why nothing changed.
    /// </summary>
    public static string? Grant(string folder, SecurityIdentifier account, AclRight right)
    {
        using var handle = OpenFolder(folder, out var problem);
        if (problem is not null) return problem;

        var security = new HandleSecurity(handle);
        security.AddAccessRule(new AccessRule<FileSystemRights>(account, Rights(right), Inherited, PropagationFlags.None, AccessControlType.Allow));
        security.Save(handle);
        return null;
    }

    /// <summary>
    /// Leaves changing the folder to SYSTEM, administrators, TrustedInstaller and <paramref name="user"/>. The folder stops
    /// inheriting from the drive, every other account that could change it keeps only read and execute, and the user gets
    /// full control, all inherited by everything below. Opened like <see cref="Grant"/>. Null on success, otherwise why
    /// nothing changed.
    /// </summary>
    public static string? Secure(string folder, SecurityIdentifier user)
    {
        using var handle = OpenFolder(folder, out var problem);
        if (problem is not null) return problem;

        var security = new HandleSecurity(handle);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<AccessRule<FileSystemRights>>().ToList();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var account in rules.Select(r => r.IdentityReference).Distinct()) security.PurgeAccessRules(account);
        foreach (var rule in rules)
        {
            var sid = (SecurityIdentifier)rule.IdentityReference;
            var writes = rule.AccessControlType == AccessControlType.Allow && (rule.Rights & WriteRights) != 0
                         && !ServiceFolderCheck.IsTrusted(sid.Value) && sid != user;
            security.AddAccessRule(new AccessRule<FileSystemRights>(sid, writes ? Rights(AclRight.ReadExecute) : rule.Rights,
                rule.InheritanceFlags, rule.PropagationFlags, rule.AccessControlType));
        }

        security.AddAccessRule(new AccessRule<FileSystemRights>(user, FileSystemRights.FullControl, Inherited, PropagationFlags.None, AccessControlType.Allow));
        security.Save(handle);
        return null;
    }

    private const InheritanceFlags Inherited = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    /// <summary>
    /// Opens the folder itself, never a link's target, with the rights to change its permissions, and checks it is a real
    /// folder reached without links. The handle stays open so the folder cannot be swapped for a link meanwhile.
    /// </summary>
    private static SafeFileHandle OpenFolder(string folder, out string? problem)
    {
        var handle = PInvoke.CreateFile(folder,
            (uint)(FILE_ACCESS_RIGHTS.READ_CONTROL | FILE_ACCESS_RIGHTS.WRITE_DAC | FILE_ACCESS_RIGHTS.FILE_READ_ATTRIBUTES),
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE, null, FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT, null);
        if (handle.IsInvalid) throw new IOException($"Anvil could not open {folder}: {new Win32Exception().Message}");
        try
        {
            if (!PInvoke.GetFileInformationByHandle(handle, out var info)) throw new Win32Exception();
            var attributes = (FileAttributes)info.dwFileAttributes;
            problem = attributes.HasFlag(FileAttributes.ReparsePoint) ? $"{folder} is a link to another folder; Anvil did not change its permissions."
                : !attributes.HasFlag(FileAttributes.Directory) ? $"{folder} is not a folder; Anvil did not change its permissions."
                : !string.Equals(FinalPath(handle), Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), StringComparison.OrdinalIgnoreCase)
                    ? $"{folder} is reached through a link to another folder; Anvil did not change its permissions."
                    : null;
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>True when the folder's entries, its own or inherited, already give <paramref name="account"/> the right.</summary>
    public static bool Allows(string folder, SecurityIdentifier account, AclRight right)
    {
        if (!Directory.Exists(folder)) return false;
        try
        {
            var granted = Rules(new DirectoryInfo(folder).GetAccessControl())
                .Where(r => r.AccessControlType == AccessControlType.Allow && r.IdentityReference.Equals(account)
                            && !r.PropagationFlags.HasFlag(PropagationFlags.InheritOnly))
                .Aggregate((FileSystemRights)0, (all, r) => all | r.FileSystemRights);
            var needed = Rights(right);
            return (granted & needed) == needed;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static FileSystemRights Rights(AclRight right) => right switch
    {
        AclRight.Modify => FileSystemRights.Modify | FileSystemRights.Synchronize,
        AclRight.ReadExecute => FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
        _ => FileSystemRights.Read | FileSystemRights.Synchronize,
    };

    private static IEnumerable<FileSystemAccessRule> Rules(FileSystemSecurity security) =>
        security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();

    private static string NameOf(SecurityIdentifier sid)
    {
        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return sid.Value;
        }
    }

    /// <summary>The path Windows actually opened, without the \\?\ prefix.</summary>
    private static string FinalPath(SafeHandle handle)
    {
        Span<char> buffer = stackalloc char[1024];
        var length = PInvoke.GetFinalPathNameByHandle(handle, buffer, GETFINALPATHNAMEBYHANDLE_FLAGS.VOLUME_NAME_DOS);
        if (length == 0 || length >= buffer.Length) throw new Win32Exception();
        var path = buffer[..(int)length].ToString();
        return path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + path[8..]
            : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    /// <summary>Reads and writes the permissions through an open handle instead of a path.</summary>
    private sealed class HandleSecurity(SafeHandle handle)
        : ObjectSecurity<FileSystemRights>(true, ResourceType.FileObject, handle, AccessControlSections.Access)
    {
        public void Save(SafeHandle target) => Persist(target);
    }
}
