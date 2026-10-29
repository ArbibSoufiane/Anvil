using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Anvil.Core.Php;

/// <summary>A PHP folder Anvil did not download: one of pvm's versions, or one the user picked.</summary>
/// <param name="Toolset">The compiler toolset it was built with, e.g. "vs17", which picks the matching Xdebug build.</param>
public sealed record PhpFolderInfo(string Folder, string Version, string Toolset, bool ThreadSafe);

/// <summary>Reads what Anvil needs to know about a PHP folder from its files, without running PHP.</summary>
public static partial class PhpFolder
{
    private const ushort Amd64 = 0x8664;

    [GeneratedRegex(@"^\d+\.\d+\.\d+")]
    private static partial Regex VersionNumber();

    /// <param name="productVersion">Reads an exe's product version; the default asks Windows.</param>
    /// <exception cref="ArgumentException">The folder is not a 64-bit PHP Anvil can run; the message says why.</exception>
    public static PhpFolderInfo Inspect(string folder, Func<string, string?>? productVersion = null)
    {
        folder = Path.GetFullPath(folder);
        var cgi = Path.Combine(folder, "php-cgi.exe");
        if (!File.Exists(cgi)) throw new ArgumentException($"{folder} has no php-cgi.exe, so it is not a PHP folder Anvil can run.");

        var (machine, linkerMajor, linkerMinor) = Header(cgi);
        if (machine != Amd64) throw new ArgumentException($"The PHP in {folder} is 32-bit. Anvil runs 64-bit PHP only.");

        var read = productVersion ?? (exe => FileVersionInfo.GetVersionInfo(exe).ProductVersion);
        var version = VersionNumber().Match(read(cgi) ?? "");
        if (!version.Success) throw new ArgumentException($"Anvil could not read the PHP version of {cgi}.");

        var threadSafe = Directory.EnumerateFiles(folder, "php*ts.dll").Any();
        return new PhpFolderInfo(folder, version.Value, Toolset(linkerMajor, linkerMinor), threadSafe);
    }

    // Visual Studio 2017 links as 14.1x, 2019 as 14.2x, 2022 and later as 14.3x and up; PHP names its builds after them.
    private static string Toolset(byte major, byte minor) => (major, minor) switch
    {
        (< 14, _) or (14, < 10) => "vc14",
        (14, < 20) => "vc15",
        (14, < 30) => "vs16",
        _ => "vs17",
    };

    private static (ushort Machine, byte LinkerMajor, byte LinkerMinor) Header(string exe)
    {
        var image = new byte[4096];
        using (var stream = File.OpenRead(exe)) image = image[..stream.ReadAtLeast(image, image.Length, throwOnEndOfStream: false)];
        var pe = image.Length >= 0x40 ? BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3C)) : -1;
        if (image.Length < 2 || image[0] != 'M' || image[1] != 'Z' || pe < 0 || pe + 28 > image.Length || !image.AsSpan(pe, 4).SequenceEqual("PE\0\0"u8))
            throw new ArgumentException($"{exe} is not a Windows program file.");
        return (BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 4)), image[pe + 24 + 2], image[pe + 24 + 3]);
    }
}

/// <summary>pvm keeps each PHP version it installed in its own folder under %USERPROFILE%\.pvm\versions.</summary>
public static class Pvm
{
    public static string VersionsDir(string userProfile) => Path.Combine(userProfile, ".pvm", "versions");

    /// <summary>Each version pvm has, newest first. Of a thread-safe and a non-thread-safe build of one version, the
    /// non-thread-safe one, which suits PHP that runs as FastCGI.</summary>
    public static IReadOnlyList<PhpFolderInfo> Find(string userProfile, Func<string, string?>? productVersion = null)
    {
        var dir = VersionsDir(userProfile);
        if (!Directory.Exists(dir)) return [];
        var found = new List<PhpFolderInfo>();
        foreach (var folder in Directory.EnumerateDirectories(dir))
        {
            try { found.Add(PhpFolder.Inspect(folder, productVersion)); }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { } // not a PHP Anvil can run
        }
        return found
            .GroupBy(f => f.Version, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(f => f.ThreadSafe).ThenBy(f => f.Folder, StringComparer.OrdinalIgnoreCase).First())
            .OrderByDescending(f => f.Version, Comparer<string>.Create(Components.SemVer.Compare))
            .ToList();
    }
}
