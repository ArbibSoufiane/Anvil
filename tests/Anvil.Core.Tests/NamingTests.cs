using System.Text.RegularExpressions;
using Anvil.Core.Tests.Support;

namespace Anvil.Core.Tests;

public sealed class NamingTests
{
    // Lines that name the old program on purpose: the ArbiPache 3 import, and cleanup of files ArbiPache 4 test builds left.
    private static readonly Regex Allowed = new(@"ArbiPache 3|ARBIPACHE_HOME|\.arbipache\b|legacy-name", RegexOptions.IgnoreCase);

    private static readonly string[] Extensions = [".cs", ".axaml", ".sbn", ".csproj", ".manifest"];

    [Fact]
    public void Old_name_appears_only_where_it_refers_to_the_old_program()
    {
        var sep = Path.DirectorySeparatorChar;
        var offenders = Directory.EnumerateFiles(Repo.Path("src"), "*.*", SearchOption.AllDirectories)
            .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: f, Number: i + 1, Line: line)))
            .Where(x => x.Line.Contains("arbipache", StringComparison.OrdinalIgnoreCase) && !Allowed.IsMatch(x.Line))
            .Select(x => $"{Path.GetRelativePath(Repo.Root, x.File)}:{x.Number}")
            .ToList();
        Assert.Empty(offenders);
    }
}
