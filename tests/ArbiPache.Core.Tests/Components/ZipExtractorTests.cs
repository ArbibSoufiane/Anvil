using System.IO.Compression;
using ArbiPache.Core.Components;

namespace ArbiPache.Core.Tests.Components;

public sealed class ZipExtractorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ap-zip-" + Guid.NewGuid().ToString("N"));

    public ZipExtractorTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string MakeZip(params string[] entries)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var name in entries)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(name);
        }
        return path;
    }

    [Fact]
    public void ZipSlip_entry_rejected()
    {
        var zip = MakeZip("ok.txt", "..\\evil.txt");
        var dest = Path.Combine(_dir, "out");

        Assert.Throws<InvalidDataException>(() => ZipExtractor.Extract(zip, dest));
        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(dest, "ok.txt")));
    }

    [Fact]
    public void Absolute_entry_rejected()
    {
        var zip = MakeZip("C:/Windows/evil.txt");

        Assert.Throws<InvalidDataException>(() => ZipExtractor.Extract(zip, Path.Combine(_dir, "out")));
    }

    [Fact]
    public void StripRoot_removes_leading_folder()
    {
        var zip = MakeZip("Apache24/bin/httpd.exe", "Apache24/conf/httpd.conf", "ReadMe.txt");
        var dest = Path.Combine(_dir, "out");

        ZipExtractor.Extract(zip, dest, stripRoot: "Apache24");

        Assert.Equal("Apache24/bin/httpd.exe", File.ReadAllText(Path.Combine(dest, "bin", "httpd.exe")));
        Assert.True(File.Exists(Path.Combine(dest, "conf", "httpd.conf")));
        Assert.False(File.Exists(Path.Combine(dest, "ReadMe.txt")));
    }

    [Fact]
    public void Extract_without_strip_keeps_layout()
    {
        var zip = MakeZip("php.exe", "ext/php_curl.dll");
        var dest = Path.Combine(_dir, "out");

        ZipExtractor.Extract(zip, dest);

        Assert.True(File.Exists(Path.Combine(dest, "php.exe")));
        Assert.True(File.Exists(Path.Combine(dest, "ext", "php_curl.dll")));
    }
}
