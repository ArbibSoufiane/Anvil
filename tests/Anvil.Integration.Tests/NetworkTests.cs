using System.Diagnostics;
using Anvil.Core.Network;

namespace Anvil.Integration.Tests;

// This PC's real adapters and Windows' network categories; read only.
public sealed class NetworkTests
{
    private static readonly Dictionary<string, int> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Public"] = 0, ["Private"] = 1, ["DomainAuthenticated"] = 2,
    };

    /// <summary>Windows' own answer, by adapter name, from Get-NetConnectionProfile.</summary>
    private static Dictionary<string, int> FromPowerShell()
    {
        using var ps = Process.Start(new ProcessStartInfo("powershell.exe",
            "-NoProfile -Command \"Get-NetConnectionProfile | ForEach-Object { $_.InterfaceAlias + '|' + $_.NetworkCategory }\"")
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        })!;
        var output = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit();
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('|')).Where(p => p.Length == 2 && Categories.ContainsKey(p[1]))
            .ToDictionary(p => p[0], p => Categories[p[1]], StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Lan_adapters_get_the_category_windows_shows()
    {
        var lan = LanAddresses.Find();
        var categories = NetworkCategory.AdapterCategories();
        var windows = FromPowerShell();

        foreach (var a in lan.Where(a => windows.ContainsKey(a.AdapterName)))
        {
            Assert.True(categories.TryGetValue(a.AdapterId, out var category), $"{a.AdapterName} {a.AdapterId} has no category");
            Assert.Equal(windows[a.AdapterName], category);
            Assert.Equal(category == 0, NetworkCategory.PublicAdapterIds().Contains(a.AdapterId));
        }
    }
}
