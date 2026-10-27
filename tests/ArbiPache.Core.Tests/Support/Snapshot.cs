using System.Runtime.CompilerServices;

namespace ArbiPache.Core.Tests.Support;

/// <summary>
/// Compares text against Snapshots\&lt;name&gt;.verified.txt beside the test file. On a difference or a new
/// snapshot it writes &lt;name&gt;.received.txt and fails; review it and rename it to .verified.txt to accept.
/// </summary>
internal static class Snapshot
{
    public static void Match(string actual, string name, [CallerFilePath] string testFile = "")
    {
        var dir = Path.Combine(Path.GetDirectoryName(testFile)!, "Snapshots");
        var verified = Path.Combine(dir, name + ".verified.txt");
        var received = Path.Combine(dir, name + ".received.txt");
        var normalized = actual.ReplaceLineEndings("\n");

        if (File.Exists(verified) && File.ReadAllText(verified).ReplaceLineEndings("\n") == normalized)
        {
            if (File.Exists(received)) File.Delete(received);
            return;
        }
        Directory.CreateDirectory(dir);
        File.WriteAllText(received, normalized);
        Assert.Fail(File.Exists(verified)
            ? $"Snapshot '{name}' changed. Compare {received} with {verified}."
            : $"New snapshot '{name}'. Review {received} and rename it to {Path.GetFileName(verified)}.");
    }
}
