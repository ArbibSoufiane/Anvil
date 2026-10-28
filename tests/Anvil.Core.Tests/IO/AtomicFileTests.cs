using System.Text;
using Anvil.Core.IO;

namespace Anvil.Core.Tests.IO;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ap-atomic-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(_dir)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Replaces_content_and_leaves_no_temp_file()
    {
        var f = Path.Combine(_dir, "hosts");
        File.WriteAllText(f, "old");

        AtomicFile.ReplaceText(f, "new", Encoding.Latin1);

        Assert.Equal("new", File.ReadAllText(f, Encoding.Latin1));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Keeps_the_files_attributes()
    {
        var f = Path.Combine(_dir, "hosts");
        File.WriteAllText(f, "old");
        File.SetAttributes(f, FileAttributes.Archive | FileAttributes.Hidden);

        AtomicFile.ReplaceText(f, "new", Encoding.Latin1);

        Assert.True(File.GetAttributes(f).HasFlag(FileAttributes.Hidden));
        Assert.Equal("new", File.ReadAllText(f, Encoding.Latin1));
    }

    [Fact]
    public void Writes_bytes_in_the_given_encoding()
    {
        var f = Path.Combine(_dir, "hosts");
        File.WriteAllText(f, "old");

        AtomicFile.ReplaceText(f, "café", Encoding.Latin1);

        Assert.Equal([0x63, 0x61, 0x66, 0xE9], File.ReadAllBytes(f));
    }

    [Fact]
    public void Creates_a_file_that_is_not_there_yet()
    {
        var f = Path.Combine(_dir, "hosts");

        AtomicFile.ReplaceText(f, "new", Encoding.Latin1);

        Assert.Equal("new", File.ReadAllText(f, Encoding.Latin1));
        Assert.Single(Directory.GetFiles(_dir));
    }
}
