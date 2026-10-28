using Anvil.Core.Platform;
using Microsoft.Win32;

namespace Anvil.App.Tests.Support;

public sealed class NoRegistry : IRegistryReader
{
    public object? GetValue(RegistryHive hive, string path, string name) => null;
}
