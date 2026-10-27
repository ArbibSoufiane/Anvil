using System.Text;

namespace ArbiPache.Elevator;

/// <summary>
/// Writes into folders the signed-in user can change. Running as administrator, the Elevator never writes through a
/// file or folder link and never overwrites: a planted link or file makes the write fail instead.
/// </summary>
internal static class SafeFile
{
    public static void CreateNew(string path, string text, Encoding encoding)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(path)!);
        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"{dir.FullName} is a link to another folder.");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, encoding);
        writer.Write(text);
    }
}
