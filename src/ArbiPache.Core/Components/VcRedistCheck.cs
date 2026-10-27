using ArbiPache.Core.Platform;
using Microsoft.Win32;

namespace ArbiPache.Core.Components;

public enum VcRedistState { Ok, Missing, Outdated }

/// <summary>
/// Checks the Visual C++ 2017-2026 x64 runtime that Apache, PHP and the databases load.
/// ArbiPache only points the user at Microsoft's page; it never downloads or runs the redistributable.
/// </summary>
public sealed class VcRedistCheck(IRegistryReader reg)
{
    private const string Key = @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64";

    /// <summary>Apache Lounge's VS18 builds ask for 14.51.36247 or newer.</summary>
    public static Version Minimum { get; } = new(14, 51, 36247);

    public static Uri DownloadPage { get; } = new("https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist");

    public VcRedistState Check()
    {
        if (reg.GetValue(RegistryHive.LocalMachine, Key, "Installed") is not int installed || installed != 1)
            return VcRedistState.Missing;
        var text = (reg.GetValue(RegistryHive.LocalMachine, Key, "Version") as string)?.TrimStart('v', 'V');
        if (!System.Version.TryParse(text, out var version)) return VcRedistState.Missing;
        return version >= Minimum ? VcRedistState.Ok : VcRedistState.Outdated;
    }
}
