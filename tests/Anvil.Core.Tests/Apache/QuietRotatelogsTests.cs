using System.Buffers.Binary;
using Anvil.Core.Apache;

namespace Anvil.Core.Tests.Apache;

/// <summary>Apache's worker process has no console, so a console program it starts opens a console window of its own.</summary>
public sealed class QuietRotatelogsTests : IDisposable
{
    private const int PeHeader = 0x80;
    private const int SubsystemField = PeHeader + 24 + 68;

    private readonly string _root = Directory.CreateTempSubdirectory("quiet-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // "MZ", the PE header's offset at 0x3C, "PE\0\0", the 20-byte file header, then the optional header: its magic says
    // 32 or 64 bit, and its Subsystem field (offset 68 in both) says 3 for a console program.
    private static byte[] ConsoleProgram(ushort magic = 0x20B)
    {
        var image = new byte[0x200];
        "MZ"u8.CopyTo(image);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), PeHeader);
        "PE\0\0"u8.CopyTo(image.AsSpan(PeHeader));
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(PeHeader + 24), magic);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(SubsystemField), 3);
        return image;
    }

    private static ushort Subsystem(string file) => BinaryPrimitives.ReadUInt16LittleEndian(File.ReadAllBytes(file).AsSpan(SubsystemField));

    private string Bin(byte[] rotatelogs)
    {
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllBytes(Path.Combine(bin, "rotatelogs.exe"), rotatelogs);
        return bin;
    }

    [Theory]
    [InlineData((ushort)0x10B)]
    [InlineData((ushort)0x20B)]
    public void The_copy_is_a_windows_program_and_the_original_is_untouched(ushort magic)
    {
        var bin = Bin(ConsoleProgram(magic));

        QuietRotatelogs.Ensure(_root);

        Assert.Equal(2, Subsystem(Path.Combine(bin, QuietRotatelogs.FileName)));
        Assert.Equal(3, Subsystem(Path.Combine(bin, "rotatelogs.exe")));
    }

    // A log helper of the Apache still running holds the copy open.
    [Fact]
    public void A_copy_in_use_that_is_already_right_is_left_alone()
    {
        var bin = Bin(ConsoleProgram());
        QuietRotatelogs.Ensure(_root);
        using var running = File.Open(Path.Combine(bin, QuietRotatelogs.FileName), FileMode.Open, FileAccess.Read, FileShare.Read);

        QuietRotatelogs.Ensure(_root);

        Assert.Equal(2, Subsystem(Path.Combine(bin, QuietRotatelogs.FileName)));
    }

    [Fact]
    public void A_changed_rotatelogs_replaces_the_copy()
    {
        var bin = Bin(ConsoleProgram());
        QuietRotatelogs.Ensure(_root);
        var changed = ConsoleProgram();
        changed[0x1FF] = 0x42;
        File.WriteAllBytes(Path.Combine(bin, "rotatelogs.exe"), changed);

        QuietRotatelogs.Ensure(_root);

        var copy = File.ReadAllBytes(Path.Combine(bin, QuietRotatelogs.FileName));
        Assert.Equal(0x42, copy[0x1FF]);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(copy.AsSpan(SubsystemField)));
    }

    [Fact]
    public void A_file_that_is_not_a_program_is_refused()
    {
        Bin("not a program"u8.ToArray());

        Assert.Throws<InvalidDataException>(() => QuietRotatelogs.Ensure(_root));
    }
}
