using Microsoft.Win32;

namespace Anvil.Core.Platform;

public interface IRegistryReader
{
    /// <summary>The value, or null when the key or value does not exist.</summary>
    object? GetValue(RegistryHive hive, string path, string name);
}
