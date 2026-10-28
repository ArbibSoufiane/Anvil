using System.Text;

namespace Anvil.Core.IO;

/// <summary>Writes a file so readers see either the old content or the new content, never half of it.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Replaces a file Windows owns (the hosts file): writes <c>&lt;path&gt;.anvil-tmp</c> beside it, then swaps it in
    /// with <see cref="File.Replace(string, string, string?)"/>, which keeps the file's ACL and attributes. A crash
    /// before the swap leaves the old file whole.
    /// </summary>
    public static void ReplaceText(string path, string text, Encoding encoding)
    {
        var tmp = path + ".anvil-tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(encoding.GetBytes(text));
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(tmp, path, destinationBackupFileName: null);
            else File.Move(tmp, path);
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
}
