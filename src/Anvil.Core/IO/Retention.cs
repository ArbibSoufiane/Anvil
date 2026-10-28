namespace Anvil.Core.IO;

/// <summary>What the Janitor deletes: logs nobody wrote for a while.</summary>
public static class Retention
{
    /// <summary>The files last written more than <paramref name="maxAge"/> before <paramref name="now"/>.</summary>
    public static IReadOnlyList<string> Expired(IEnumerable<FileInfo> files, DateTimeOffset now, TimeSpan maxAge) =>
        files.Where(f => now - new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero) > maxAge).Select(f => f.FullName).ToList();
}
