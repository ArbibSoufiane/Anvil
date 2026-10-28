using System.Text.Json;

namespace Anvil.Core.Tests.Packaging;

/// <summary>
/// Checks the folder build\publish.ps1 produces, dist\Anvil\. Skipped until the script has run; the script runs
/// these again on its fresh output before it zips anything.
/// </summary>
public sealed class PublishLayoutTests
{
    private static readonly string[] Programs = ["Anvil.exe", "Anvil.Agent.exe", "Anvil.Elevator.exe"];

    // HostWriter's marker in every apphost: 8 bytes of bundle header offset (zero unless files are bundled into the
    // exe), then the SHA-256 of ".net core bundle".
    private static readonly byte[] BundleSignature =
    [
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae,
    ];

    private static string Dist()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anvil.sln"))) dir = dir.Parent;
        var dist = dir is null ? null : Path.Combine(dir.FullName, "dist", "Anvil");
        if (dist is null || !File.Exists(Path.Combine(dist, "Anvil.exe")))
        {
            // publish.ps1 sets this, so its own check cannot pass by skipping.
            Assert.False(Environment.GetEnvironmentVariable("ANVIL_REQUIRE_DIST") == "1", $"No published Anvil.exe in {dist}.");
            Assert.Skip("No dist\\Anvil yet: run build\\publish.ps1 first.");
        }
        return dist;
    }

    [Fact]
    public void Three_exes_present()
    {
        var dist = Dist();

        Assert.All(Programs, p => Assert.True(File.Exists(Path.Combine(dist, p)), $"{p} is missing"));
    }

    // A single-file exe unpacks itself at run time, which is what Defender's heuristics dislike most.
    [Fact]
    public void No_single_file_bundle()
    {
        var dist = Dist();

        var bundles = Directory.EnumerateFiles(dist, "*.exe", SearchOption.AllDirectories).Where(IsBundle).Select(Path.GetFileName).ToList();

        Assert.Empty(bundles);
    }

    [Fact]
    public void Catalog_shipped()
    {
        var dist = Dist();
        var shipped = Path.Combine(dist, "catalog", "components.json");

        Assert.True(File.Exists(shipped));
        using var _ = JsonDocument.Parse(File.ReadAllBytes(shipped));
        Assert.Equal(File.ReadAllBytes(Path.Combine(dist, "..", "..", "catalog", "components.json")), File.ReadAllBytes(shipped));
    }

    // What the user owns stays out of a release, so an update can never replace it.
    [Fact]
    public void No_runtime_or_data_folders_shipped()
    {
        var dist = Dist();

        Assert.All(new[] { "runtime", "data", "htdocs", ".update" }, d => Assert.False(Directory.Exists(Path.Combine(dist, d)), $"{d}\\ is shipped"));
    }

    [Fact]
    public void Readme_and_licenses_shipped()
    {
        var dist = Dist();

        Assert.All(new[] { "README.txt", "LICENSE.txt", @"licenses\IBM-Plex-OFL.txt" },
            f => Assert.True(File.Exists(Path.Combine(dist, f)), $"{f} is missing"));
    }

    internal static bool IsBundle(string exe)
    {
        var bytes = File.ReadAllBytes(exe);
        var at = bytes.AsSpan().IndexOf(BundleSignature);
        return at >= 8 && BitConverter.ToInt64(bytes, at - 8) != 0;
    }
}
