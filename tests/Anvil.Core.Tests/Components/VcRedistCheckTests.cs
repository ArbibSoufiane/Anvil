using Anvil.Core.Components;
using Anvil.Core.Platform;
using Microsoft.Win32;

namespace Anvil.Core.Tests.Components;

public sealed class VcRedistCheckTests
{
    private const string Key = @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64";

    private static VcRedistCheck With(object? installed, object? version) =>
        new(new FakeRegistry(new Dictionary<string, object?> { ["Installed"] = installed, ["Version"] = version }));

    [Fact]
    public void Missing_key_is_Missing() =>
        Assert.Equal(VcRedistState.Missing, new VcRedistCheck(new FakeRegistry([])).Check());

    [Fact]
    public void Not_installed_flag_is_Missing() =>
        Assert.Equal(VcRedistState.Missing, With(0, "v14.51.36247.00").Check());

    [Fact]
    public void Version_below_minimum_is_Outdated() =>
        Assert.Equal(VcRedistState.Outdated, With(1, "v14.44.35211.00").Check());

    [Fact]
    public void Version_14_51_is_Ok() =>
        Assert.Equal(VcRedistState.Ok, With(1, "v14.51.36247.00").Check());

    [Fact]
    public void Newer_version_is_Ok() =>
        Assert.Equal(VcRedistState.Ok, With(1, "v14.52.10000.00").Check());

    [Fact]
    public void Download_page_is_microsoft_https() =>
        Assert.Equal("https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist", VcRedistCheck.DownloadPage.ToString());

    private sealed class FakeRegistry(Dictionary<string, object?> values) : IRegistryReader
    {
        public object? GetValue(RegistryHive hive, string path, string name) =>
            hive == RegistryHive.LocalMachine && path == Key && values.TryGetValue(name, out var v) ? v : null;
    }
}
