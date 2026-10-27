using System.Text.RegularExpressions;
using ArbiPache.Core.Components;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Tests.Support;

namespace ArbiPache.Core.Tests.Components;

public sealed class CatalogTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ap-cat-" + Guid.NewGuid().ToString("N")));

    public CatalogTests() => _paths.EnsureCreated();

    public void Dispose() => Directory.Delete(_paths.Root, recursive: true);

    private static ComponentRelease Apache(string version, string sha = "a") =>
        new(ComponentKind.Apache, version, new Uri($"https://example.com/httpd-{version}.zip"), sha.PadRight(64, '0'));

    private void WriteShipped(string json) => File.WriteAllText(Path.Combine(_paths.Catalog, "components.json"), json);

    [Fact]
    public async Task Releases_sorted_newest_first()
    {
        var online = new FakeProvider(ComponentKind.Apache, Apache("2.4.9"), Apache("2.4.68"), Apache("2.4.66"));
        var catalog = new ComponentCatalog([online], new ShippedCatalogProvider(_paths));

        var releases = await catalog.GetAsync(ComponentKind.Apache, TestContext.Current.CancellationToken);

        Assert.Equal(["2.4.68", "2.4.66", "2.4.9"], releases.Select(r => r.Version));
    }

    [Fact]
    public void Shipped_catalog_entries_are_https_with_64_hex_sha256()
    {
        var shipped = new ShippedCatalogProvider(new AppPaths(Repo.Path("."))).All();

        Assert.NotEmpty(shipped);
        Assert.Contains(shipped, r => r.Kind == ComponentKind.Apache);
        Assert.Contains(shipped, r => r.Kind == ComponentKind.CaBundle);
        Assert.All(shipped, r =>
        {
            Assert.Equal(Uri.UriSchemeHttps, r.Url.Scheme);
            Assert.Matches(new Regex("^[0-9a-f]{64}$"), r.Sha256);
        });
    }

    [Fact]
    public async Task Online_failure_falls_back_to_shipped()
    {
        WriteShipped("""
            {"schemaVersion":1,"releases":[
              {"kind":"Apache","version":"2.4.69","url":"https://example.com/a.zip","sha256":"0000000000000000000000000000000000000000000000000000000000000001","toolset":"VS18","stripRoot":"Apache24"}
            ]}
            """);
        var catalog = new ComponentCatalog([new FailingProvider(ComponentKind.Apache)], new ShippedCatalogProvider(_paths));

        var releases = await catalog.GetAsync(ComponentKind.Apache, TestContext.Current.CancellationToken);

        var only = Assert.Single(releases);
        Assert.Equal("2.4.69", only.Version);
        Assert.Equal("Apache24", only.StripRoot);
    }

    [Fact]
    public async Task Duplicate_versions_prefer_online_entry()
    {
        WriteShipped("""
            {"schemaVersion":1,"releases":[
              {"kind":"Apache","version":"2.4.69","url":"https://example.com/shipped.zip","sha256":"0000000000000000000000000000000000000000000000000000000000000001"}
            ]}
            """);
        var online = new FakeProvider(ComponentKind.Apache, Apache("2.4.69", "b"));
        var catalog = new ComponentCatalog([online], new ShippedCatalogProvider(_paths));

        var releases = await catalog.GetAsync(ComponentKind.Apache, TestContext.Current.CancellationToken);

        Assert.Equal("https://example.com/httpd-2.4.69.zip", Assert.Single(releases).Url.ToString());
    }

    [Fact]
    public async Task Other_kinds_are_filtered_out()
    {
        WriteShipped("""
            {"schemaVersion":1,"releases":[
              {"kind":"CaBundle","version":"2026-10-20","url":"https://curl.se/ca/cacert-2026-10-20.pem","sha256":"0000000000000000000000000000000000000000000000000000000000000001"}
            ]}
            """);
        var catalog = new ComponentCatalog([], new ShippedCatalogProvider(_paths));

        Assert.Empty(await catalog.GetAsync(ComponentKind.Apache, TestContext.Current.CancellationToken));
        Assert.Single(await catalog.GetAsync(ComponentKind.CaBundle, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("2.4.68", "2.4.9", 1)]
    [InlineData("8.5.0", "8.5.0", 0)]
    [InlineData("8.5.0RC1", "8.5.0", -1)]
    [InlineData("11.4.8", "11.8.3", -1)]
    [InlineData("8.4", "8.4.0", -1)]
    public void SemVer_compares_numerically(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(SemVer.Compare(a, b)));

    private sealed class FakeProvider(ComponentKind kind, params ComponentRelease[] releases) : ICatalogProvider
    {
        public ComponentKind Kind => kind;
        public Task<IReadOnlyList<ComponentRelease>> GetAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ComponentRelease>>(releases);
    }

    private sealed class FailingProvider(ComponentKind kind) : ICatalogProvider
    {
        public ComponentKind Kind => kind;
        public Task<IReadOnlyList<ComponentRelease>> GetAsync(CancellationToken ct) => throw new HttpRequestException("offline");
    }
}
