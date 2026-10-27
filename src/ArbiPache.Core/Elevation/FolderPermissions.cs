using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace ArbiPache.Core.Elevation;

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
        using var handle = PInvoke.CreateFile(folder,
            (uint)(FILE_ACCESS_RIGHTS.READ_CONTROL | FILE_ACCESS_RIGHTS.WRITE_DAC | FILE_ACCESS_RIGHTS.FILE_READ_ATTRIBUTES),
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE, null, FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT, null);
        if (handle.IsInvalid) throw new IOException($"ArbiPache could not open {folder}: {new Win32Exception().Message}");
        if (!PInvoke.GetFileInformationByHandle(handle, out var info)) throw new Win32Exception();

        var attributes = (FileAttributes)info.dwFileAttributes;
        if (attributes.HasFlag(FileAttributes.ReparsePoint)) return $"{folder} is a link to another folder; ArbiPache did not change its permissions.";
        if (!attributes.HasFlag(FileAttributes.Directory)) return $"{folder} is not a folder; ArbiPache did not change its permissions.";
        if (!string.Equals(FinalPath(handle), Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), StringComparison.OrdinalIgnoreCase))
            return $"{folder} is reached through a link to another folder; ArbiPache did not change its permissions.";

        var security = new HandleSecurity(handle);
        security.AddAccessRule(new AccessRule<FileSystemRights>(account, Rights(right),
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.Save(handle);
        return null;
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
