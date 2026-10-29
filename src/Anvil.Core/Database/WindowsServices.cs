using Microsoft.Win32;

namespace Anvil.Core.Database;

/// <summary>Windows' list of services, read from the registry, which any account may read.</summary>
public static class WindowsServices
{
    private const string Services = @"SYSTEM\CurrentControlSet\Services";

    /// <summary>A service's command line, or null when there is no such service.</summary>
    public static string? ImagePath(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{Services}\{name}");
        return key?.GetValue("ImagePath") as string;
    }

    /// <summary>Every service with a command line, as (name, ImagePath).</summary>
    public static IReadOnlyList<(string Name, string ImagePath)> All()
    {
        using var root = Registry.LocalMachine.OpenSubKey(Services);
        if (root is null) return [];
        var all = new List<(string, string)>();
        foreach (var name in root.GetSubKeyNames())
        {
            try
            {
                using var key = root.OpenSubKey(name);
                if (key?.GetValue("ImagePath") is string path) all.Add((name, path));
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { } // a key this account may not read
        }
        return all;
    }
}
