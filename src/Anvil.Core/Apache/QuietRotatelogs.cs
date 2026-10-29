using System.Buffers.Binary;

namespace Anvil.Core.Apache;

/// <summary>
/// Apache's worker process runs without a console, so Windows gives each console program it starts, rotatelogs among
/// them, a console window of its own: one per log, again after every restart. A copy of rotatelogs marked as a Windows
/// program writes the logs without one. It sits beside the original, which needs Apache's DLLs.
/// </summary>
public static class QuietRotatelogs
{
    public const string FileName = "anvil-rotatelogs.exe";

    private const ushort WindowsProgram = 2;
    private const ushort ConsoleProgram = 3;

    /// <summary>Creates or refreshes the copy in <paramref name="apacheRoot"/>\bin.</summary>
    /// <exception cref="InvalidDataException">rotatelogs.exe is not a Windows program file.</exception>
    public static void Ensure(string apacheRoot)
    {
        var bin = Path.Combine(apacheRoot, "bin");
        var image = File.ReadAllBytes(Path.Combine(bin, "rotatelogs.exe"));
        MarkAsWindowsProgram(image);

        // A running Apache's log helpers hold the copy open; an identical copy needs no write.
        var target = Path.Combine(bin, FileName);
        if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(image)) return;
        var partial = target + ".partial";
        File.WriteAllBytes(partial, image);
        File.Move(partial, target, overwrite: true);
    }

    // The PE header's offset is at 0x3C. After "PE\0\0" and the 20-byte file header comes the optional header, whose
    // first field says 32 or 64 bit and whose Subsystem field is at offset 68 in both.
    private static void MarkAsWindowsProgram(byte[] image)
    {
        var span = image.AsSpan();
        if (span.Length < 0x40 || !span.StartsWith("MZ"u8)) throw NotAProgram();
        var pe = BinaryPrimitives.ReadInt32LittleEndian(span[0x3C..]);
        var subsystemAt = (long)pe + 24 + 68;
        if (pe < 0x40 || subsystemAt + 2 > span.Length || !span[pe..].StartsWith("PE\0\0"u8)) throw NotAProgram();
        var magic = BinaryPrimitives.ReadUInt16LittleEndian(span[(pe + 24)..]);
        var subsystem = BinaryPrimitives.ReadUInt16LittleEndian(span[(int)subsystemAt..]);
        if (magic is not (0x10B or 0x20B) || subsystem is not (ConsoleProgram or WindowsProgram)) throw NotAProgram();
        BinaryPrimitives.WriteUInt16LittleEndian(span[(int)subsystemAt..], WindowsProgram);
    }

    private static InvalidDataException NotAProgram() => new("rotatelogs.exe is not a Windows program file.");
}
