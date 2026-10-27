using Microsoft.Win32;

namespace ArbiPache.Core.Platform;

public interface IRegistryReader
{
    /// <summary>The value, or null when the key or value does not exist.</summary>
    object? GetValue(RegistryHive hive, string path, string name);
}
