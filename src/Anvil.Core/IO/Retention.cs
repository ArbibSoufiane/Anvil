namespace Anvil.Core.IO;

/// <summary>What the Janitor deletes: logs nobody wrote for a while, and backups beyond a count.</summary>
public static class Retention
{
    /// <summary>The files last written more than <paramref name="maxAge"/> before <paramref name="now"/>.</summary>
    public static IReadOnlyList<string> Expired(IEnumerable<FileInfo> files, DateTimeOffset now, TimeSpan maxAge) =>
        files.Where(f => now - new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero) > maxAge).Select(f => f.FullName).ToList();

    /// <summary>
    /// The folders after the newest <paramref name="keep"/>. Their names are timestamps (2026-10-27-140209, then -2, -3
    /// within the same second), so numbers inside them compare as numbers.
    /// </summary>
    public static IReadOnlyList<string> BeyondNewest(IEnumerable<string> dirs, int keep) =>
        dirs.OrderByDescending(d => Path.GetFileName(Path.TrimEndingDirectorySeparator(d)), NaturalOrder.Instance).Skip(keep).ToList();

    private sealed class NaturalOrder : IComparer<string>
    {
        public static readonly NaturalOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            if (x is null || y is null) return string.CompareOrdinal(x, y);
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
                {
                    var (a, nextI) = Number(x, i);
                    var (b, nextJ) = Number(y, j);
                    if (a != b) return a.CompareTo(b);
                    (i, j) = (nextI, nextJ);
                    continue;
                }
                if (x[i] != y[j]) return x[i].CompareTo(y[j]);
                i++;
                j++;
            }
            return (x.Length - i).CompareTo(y.Length - j);
        }

        private static (decimal Value, int Next) Number(string s, int start)
        {
            var end = start;
            while (end < s.Length && char.IsAsciiDigit(s[end])) end++;
            return (decimal.Parse(s.AsSpan(start, Math.Min(end - start, 28)), System.Globalization.CultureInfo.InvariantCulture), end);
        }
    }
}
