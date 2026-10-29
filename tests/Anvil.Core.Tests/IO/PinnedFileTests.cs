using System.Diagnostics;
using System.Text;
using Anvil.Core.IO;

namespace Anvil.Core.Tests.IO;

public sealed class PinnedFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ap-pinned-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, Encoding.Latin1);
        return path;
    }

    private static void Link(Action create)
    {
        try
        {
            create();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"This account cannot create symbolic links: {e.Message}");
        }
    }

    private static void HardLink(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "/c", "mklink", "/H", link, target }) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void Rewrites_the_file_in_place()
    {
        var ini = Write("my.ini", "[mysqld]\r\nport=3307\r\n");

        var changed = PinnedFile.Rewrite(ini, Encoding.Latin1, t => t.Replace("3307", "3310", StringComparison.Ordinal));

        Assert.True(changed);
        Assert.Equal("[mysqld]\r\nport=3310\r\n", File.ReadAllText(ini, Encoding.Latin1));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void A_shorter_text_leaves_nothing_of_the_old_one()
    {
        var ini = Write("my.ini", "[mysqld]\r\nport=33070\r\n");

        PinnedFile.Rewrite(ini, Encoding.Latin1, _ => "[mysqld]\r\n");

        Assert.Equal("[mysqld]\r\n", File.ReadAllText(ini, Encoding.Latin1));
    }

    [Fact]
    public void An_unchanged_file_is_not_written()
    {
        var ini = Write("my.ini", "[mysqld]\r\nport=3307\r\n");
        var stamp = File.GetLastWriteTimeUtc(ini) - TimeSpan.FromHours(1);
        File.SetLastWriteTimeUtc(ini, stamp);

        Assert.False(PinnedFile.Rewrite(ini, Encoding.Latin1, t => t));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(ini));
    }

    [Fact]
    public void A_link_planted_beside_the_file_is_never_followed()
    {
        var ini = Write(@"data\my.ini", "[mysqld]\r\nport=3307\r\n");
        var victim = Write("victim.dll", "system file");
        Link(() => File.CreateSymbolicLink(ini + ".anvil-tmp", victim));

        PinnedFile.Rewrite(ini, Encoding.Latin1, _ => "payload");

        Assert.Equal("system file", File.ReadAllText(victim, Encoding.Latin1));
    }

    [Fact]
    public void A_file_that_is_a_link_is_refused()
    {
        var victim = Write("victim.dll", "system file");
        var ini = Path.Combine(_dir, "my.ini");
        Link(() => File.CreateSymbolicLink(ini, victim));

        var e = Assert.Throws<IOException>(() => PinnedFile.Rewrite(ini, Encoding.Latin1, _ => "payload"));

        Assert.Equal($"{ini} is a link or sits in a linked folder, so Anvil does not change it.", e.Message);
        Assert.Equal("system file", File.ReadAllText(victim, Encoding.Latin1));
    }

    [Fact]
    public void A_file_in_a_linked_folder_is_refused()
    {
        var victim = Write(@"system\my.ini", "[mysqld]\r\nport=3307\r\n");
        var data = Path.Combine(_dir, "data");
        Link(() => Directory.CreateSymbolicLink(data, Path.Combine(_dir, "system")));
        var ini = Path.Combine(data, "my.ini");

        var e = Assert.Throws<IOException>(() => PinnedFile.Rewrite(ini, Encoding.Latin1, _ => "payload"));

        Assert.Equal($"{ini} is a link or sits in a linked folder, so Anvil does not change it.", e.Message);
        Assert.Equal("[mysqld]\r\nport=3307\r\n", File.ReadAllText(victim, Encoding.Latin1));
    }

    [Fact]
    public void A_hard_linked_file_is_refused()
    {
        var victim = Write("victim.ini", "system file");
        var ini = Path.Combine(_dir, "my.ini");
        HardLink(ini, victim);

        var e = Assert.Throws<IOException>(() => PinnedFile.Rewrite(ini, Encoding.Latin1, _ => "payload"));

        Assert.Equal($"{ini} has other names on disk (hard links), so Anvil does not change it.", e.Message);
        Assert.Equal("system file", File.ReadAllText(victim, Encoding.Latin1));
    }
}
