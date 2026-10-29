using System.Buffers.Binary;
using Anvil.Core.Php;

namespace Anvil.Core.Tests.Php;

/// <summary>PHP folders Anvil did not download: pvm's versions, or any folder the user picks.</summary>
public sealed class PhpFolderTests : IDisposable
{
    private const int PeHeader = 0x80;

    private readonly string _root = Directory.CreateTempSubdirectory("phpfolder-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // The PE header's offset at 0x3C, "PE\0\0", the machine in the file header, the linker version at bytes 2 and 3 of the
    // optional header.
    private static byte[] Program(ushort machine = 0x8664, byte linkerMajor = 14, byte linkerMinor = 44)
    {
        var image = new byte[0x200];
        "MZ"u8.CopyTo(image);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), PeHeader);
        "PE\0\0"u8.CopyTo(image.AsSpan(PeHeader));
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(PeHeader + 4), machine);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(PeHeader + 24), 0x20B);
        image[PeHeader + 24 + 2] = linkerMajor;
        image[PeHeader + 24 + 3] = linkerMinor;
        return image;
    }

    private string Php(string name, string? threadSafeDll = null, byte[]? cgi = null)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "php-cgi.exe"), cgi ?? Program());
        if (threadSafeDll is not null) File.WriteAllText(Path.Combine(folder, threadSafeDll), "");
        return folder;
    }

    // The tests have no real version resource; this stands in for php-cgi.exe's product version.
    private static Func<string, string?> Versions(params (string Folder, string Version)[] map) =>
        exe => map.FirstOrDefault(m => string.Equals(Path.GetDirectoryName(exe), m.Folder, StringComparison.OrdinalIgnoreCase)).Version;

    [Theory]
    [InlineData(14, 16, "vc15")]
    [InlineData(14, 29, "vs16")]
    [InlineData(14, 44, "vs17")]
    [InlineData(14, 50, "vs17")]
    public void The_toolset_comes_from_the_linker_version(byte major, byte minor, string toolset)
    {
        var folder = Php("php", cgi: Program(linkerMajor: major, linkerMinor: minor));

        Assert.Equal(toolset, PhpFolder.Inspect(folder, Versions((folder, "8.4.27"))).Toolset);
    }

    [Fact]
    public void A_folder_reads_as_its_version_and_build()
    {
        var nts = Php("php-8.4.27-1-nts-Win32-vs17-x64");
        var ts = Php("php-8.4.27-1-Win32-vs17-x64", threadSafeDll: "php8ts.dll");
        var versions = Versions((nts, "8.4.27"), (ts, "8.4.27"));

        Assert.Equal(new PhpFolderInfo(nts, "8.4.27", "vs17", ThreadSafe: false), PhpFolder.Inspect(nts, versions));
        Assert.True(PhpFolder.Inspect(ts, versions).ThreadSafe);
    }

    [Fact]
    public void A_product_version_with_a_suffix_keeps_the_number()
    {
        var folder = Php("php");

        Assert.Equal("8.5.0", PhpFolder.Inspect(folder, Versions((folder, "8.5.0-dev"))).Version);
    }

    [Fact]
    public void A_folder_without_php_cgi_is_refused()
    {
        var folder = Path.Combine(_root, "empty");
        Directory.CreateDirectory(folder);

        var e = Assert.Throws<ArgumentException>(() => PhpFolder.Inspect(folder, _ => "8.4.27"));
        Assert.Equal($"{folder} has no php-cgi.exe, so it is not a PHP folder Anvil can run.", e.Message);
    }

    [Fact]
    public void A_32_bit_php_is_refused()
    {
        var folder = Php("php-x86", cgi: Program(machine: 0x14C));

        var e = Assert.Throws<ArgumentException>(() => PhpFolder.Inspect(folder, Versions((folder, "8.4.27"))));
        Assert.Equal($"The PHP in {folder} is 32-bit. Anvil runs 64-bit PHP only.", e.Message);
    }

    [Fact]
    public void A_php_with_no_readable_version_is_refused()
    {
        var folder = Php("php");

        var e = Assert.Throws<ArgumentException>(() => PhpFolder.Inspect(folder, _ => null));
        Assert.Equal($"Anvil could not read the PHP version of {Path.Combine(folder, "php-cgi.exe")}.", e.Message);
    }

    [Fact]
    public void Pvm_lists_each_version_once_preferring_the_non_thread_safe_build()
    {
        var versions = Path.Combine(_root, ".pvm", "versions");
        Directory.CreateDirectory(versions);
        var old = Php(Path.Combine(".pvm", "versions", "php-8.2.32-Win32-vs16-x64"), "php8ts.dll", Program(linkerMinor: 29));
        var ts = Php(Path.Combine(".pvm", "versions", "php-8.4.27-1-Win32-vs17-x64"), "php8ts.dll");
        var nts = Php(Path.Combine(".pvm", "versions", "php-8.4.27-1-nts-Win32-vs17-x64"));
        Directory.CreateDirectory(Path.Combine(versions, "not-php"));

        var found = Pvm.Find(_root, Versions((old, "8.2.32"), (ts, "8.4.27"), (nts, "8.4.27")));

        Assert.Equal([("8.4.27", nts), ("8.2.32", old)], found.Select(f => (f.Version, f.Folder)));
    }

    [Fact]
    public void Without_pvm_there_is_nothing_to_list() => Assert.Empty(Pvm.Find(_root));
}
