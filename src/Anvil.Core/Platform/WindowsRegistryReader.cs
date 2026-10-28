using Microsoft.Win32;

namespace Anvil.Core.Platform;

/// <summary>Reads the 64-bit registry view.</summary>
public sealed class WindowsRegistryReader : IRegistryReader
{
    public object? GetValue(RegistryHive hive, string path, string name)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.OpenSubKey(path);
        return key?.GetValue(name);
    }
}
