using System.Runtime.CompilerServices;
using System.Text;

namespace ArbiPache.Core.Logs;

/// <summary>
/// Reads the end of a log file and follows what is appended, while the writer (httpd, php-cgi, mariadbd) keeps it
/// open. A file that shrinks was truncated or rotated, and is read again from the start.
/// </summary>
public sealed class LogTail(string path, TimeSpan? poll = null)
{
    private const int Chunk = 64 * 1024;
    private const FileShare Sharing = FileShare.ReadWrite | FileShare.Delete;
    private readonly TimeSpan _poll = poll ?? TimeSpan.FromMilliseconds(500);

    public string Path { get; } = path;

    /// <summary>The last <paramref name="lines"/> lines, oldest first; a final line without a newline counts.</summary>
    public IReadOnlyList<string> ReadLast(int lines)
    {
        if (lines <= 0) return [];
        using var stream = TryOpen();
        if (stream is null) return [];

        // Read backwards a chunk at a time until enough line breaks are in hand.
        var end = stream.Length;
        var start = end;
        var buffer = new List<byte[]>();
        var breaks = 0;
        while (start > 0 && breaks <= lines)
        {
            var size = (int)Math.Min(Chunk, start);
            start -= size;
            var block = new byte[size];
            stream.Position = start;
            stream.ReadExactly(block);
            buffer.Insert(0, block);
            breaks += block.Count(b => b == (byte)'\n');
        }

        var text = Encoding.UTF8.GetString(buffer.SelectMany(b => b).ToArray());
        var all = Split(text, out var partial);
        if (partial.Length > 0) all.Add(partial);
        // When reading stopped mid-file, the first piece is a fragment of a longer line.
        if (start > 0 && all.Count > lines) all.RemoveAt(0);
        return all.Count <= lines ? all : all[^lines..];
    }

    /// <summary>Yields each line appended after the call, waiting for the file when it does not exist yet.</summary>
    public async IAsyncEnumerable<string> FollowAsync([EnumeratorCancellation] CancellationToken ct)
    {
        long position;
        using (var initial = TryOpen()) position = initial?.Length ?? 0;
        var pending = "";

        while (!ct.IsCancellationRequested)
        {
            List<string> ready = [];
            using (var stream = TryOpen())
            {
                if (stream is not null)
                {
                    if (stream.Length < position)
                    {
                        position = 0;
                        pending = "";
                    }
                    if (stream.Length > position)
                    {
                        stream.Position = position;
                        var block = new byte[stream.Length - position];
                        stream.ReadExactly(block);
                        position += block.Length;
                        ready = Split(pending + Encoding.UTF8.GetString(block), out pending);
                    }
                }
            }
            foreach (var line in ready) yield return line;

            try
            {
                await Task.Delay(_poll, ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    private FileStream? TryOpen()
    {
        try
        {
            return new FileStream(Path, FileMode.Open, FileAccess.Read, Sharing);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>Complete lines without their line breaks; the text after the last break is returned in <paramref name="partial"/>.</summary>
    private static List<string> Split(string text, out string partial)
    {
        var lines = new List<string>();
        var from = 0;
        for (var i = text.IndexOf('\n'); i >= 0; i = text.IndexOf('\n', from))
        {
            lines.Add(text[from..i].TrimEnd('\r'));
            from = i + 1;
        }
        partial = text[from..];
        return lines;
    }
}
