using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace Anvil.Core.IO;

/// <summary>
/// Rewrites a file an administrator changes for the user, such as a database's my.ini in a folder the user can write to,
/// without being steered to another file: neither the file nor a folder on its path may be a link, the file has one name on
/// disk, and the change goes through the handle those checks were made on. Nothing is written beside the file, so a link
/// planted there is never followed. The write is in place rather than atomic; these files are small and written in one call.
/// </summary>
public static class PinnedFile
{
    /// <returns>Whether the text changed and was written.</returns>
    /// <exception cref="IOException">The file is a link, sits in a linked folder, or has hard links.</exception>
    public static bool Rewrite(string path, Encoding encoding, Func<string, string> change)
    {
        var full = Path.GetFullPath(path);
        var linked = new IOException($"{full} is a link or sits in a linked folder, so Anvil does not change it.");
        for (var p = full; p is not null; p = Path.GetDirectoryName(p))
            if (File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)) throw linked;

        using var handle = PInvoke.CreateFile(full, (uint)(FILE_ACCESS_RIGHTS.FILE_GENERIC_READ | FILE_ACCESS_RIGHTS.FILE_GENERIC_WRITE),
            FILE_SHARE_MODE.FILE_SHARE_READ, null, FILE_CREATION_DISPOSITION.OPEN_EXISTING, FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT, null);
        if (handle.IsInvalid) Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
        if (!PInvoke.GetFileInformationByHandle(handle, out var info)) Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
        if (((FileAttributes)info.dwFileAttributes).HasFlag(FileAttributes.ReparsePoint)) throw linked;
        if (info.nNumberOfLinks != 1) throw new IOException($"{full} has other names on disk (hard links), so Anvil does not change it.");
        // A folder on the path swapped for a link after the checks above sends the handle somewhere else.
        if (!string.Equals(FinalPath(handle), full, StringComparison.OrdinalIgnoreCase)) throw linked;

        using var stream = new FileStream(handle, FileAccess.ReadWrite);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        var current = encoding.GetString(bytes);
        var next = change(current);
        if (next == current) return false;

        var written = encoding.GetBytes(next);
        stream.Position = 0;
        stream.Write(written);
        stream.SetLength(written.Length);
        stream.Flush(flushToDisk: true);
        return true;
    }

    private static unsafe string FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[32_768];
        uint length;
        fixed (char* p = buffer)
            length = PInvoke.GetFinalPathNameByHandle(new HANDLE(handle.DangerousGetHandle()), new PWSTR(p), (uint)buffer.Length,
                GETFINALPATHNAMEBYHANDLE_FLAGS.FILE_NAME_NORMALIZED);
        if (length == 0 || length >= buffer.Length) Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
        var final = new string(buffer, 0, (int)length);
        return final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
    }
}
