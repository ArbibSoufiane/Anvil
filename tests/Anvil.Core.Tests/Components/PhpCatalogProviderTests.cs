using System.Net;
using Anvil.Core.Components;
using Anvil.Core.Paths;
using Anvil.Core.Tests.Support;

namespace Anvil.Core.Tests.Components;

public sealed class PhpCatalogProviderTests
{
    // A real releases.json saved from windows.php.net on 2026-10-27.
    private static readonly string Fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Components", "Fixtures", "php-releases.json"));

    private static async Task<(IReadOnlyList<ComponentRelease> Releases, FixtureHandler Handler)> LoadAsync()
    {
        var handler = new FixtureHandler(Fixture);
        var provider = new PhpCatalogProvider(new HttpClient(handler));
        return (await provider.GetAsync(TestContext.Current.CancellationToken), handler);
    }

    [Fact]
    public async Task Parses_nts_x64_zip_with_sha256()
    {
        var (releases, _) = await LoadAsync();

        var php85 = Assert.Single(releases, r => r.Version.StartsWith("8.5.", StringComparison.Ordinal));
        Assert.Equal(ComponentKind.Php, php85.Kind);
        Assert.Equal("8.5.12", php85.Version);
        Assert.Equal(new Uri("https://downloads.php.net/~windows/releases/php-8.5.12-1-nts-Win32-vs17-x64.zip"), php85.Url);
        Assert.Equal("2a36a2edcb7a5b9252f638fd5e4cf84d7f6e066092654220dc0b63e5cf4e9723", php85.Sha256);
    }

    [Fact]
    public async Task Ignores_thread_safe_builds()
    {
        var (releases, _) = await LoadAsync();

        Assert.Equal(7, releases.Count);
        Assert.All(releases, r =>
        {
            Assert.Contains("-nts-Win32-", r.Url.AbsoluteUri, StringComparison.Ordinal);
            Assert.EndsWith("-x64.zip", r.Url.AbsoluteUri, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Toolset_recorded()
    {
        var (releases, _) = await LoadAsync();

        var toolsets = releases.ToDictionary(r => string.Join('.', r.Version.Split('.').Take(2)), r => r.Toolset);
        Assert.Equal("vc15", toolsets["7.4"]);
        Assert.Equal("vs16", toolsets["8.0"]);
        Assert.Equal("vs16", toolsets["8.3"]);
        Assert.Equal("vs17", toolsets["8.4"]);
        Assert.Equal("vs17", toolsets["8.5"]);
    }

    [Fact]
    public async Task Build_suffix_dropped_from_version()
    {
        var (releases, _) = await LoadAsync();

        Assert.Contains(releases, r => r.Version == "8.2.35" && r.Url.AbsoluteUri.EndsWith("php-8.2.35-1-nts-Win32-vs16-x64.zip", StringComparison.Ordinal));
        Assert.DoesNotContain(releases, r => r.Version.Contains('-'));
    }

    [Fact]
    public async Task Reads_the_official_feed_with_an_honest_user_agent()
    {
        var (_, handler) = await LoadAsync();

        Assert.Equal(new Uri("https://downloads.php.net/~windows/releases/releases.json"), handler.LastRequest!.RequestUri);
        Assert.StartsWith("Anvil/", handler.LastRequest.Headers.UserAgent.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Shipped_catalog_pins_end_of_life_php_and_xdebug()
    {
        var shipped = new ShippedCatalogProvider(new AppPaths(Repo.Path("."))).All();

        Assert.Equal(["7.4.33", "8.0.30", "8.1.34"], shipped.Where(r => r.Kind == ComponentKind.Php).Select(r => r.Version).Order());
        Assert.Equal(["7.4", "8.0", "8.1", "8.2", "8.3", "8.4", "8.5"],
            shipped.Where(r => r.Kind == ComponentKind.Xdebug).Select(r => r.AppliesTo).Order());
        Assert.All(shipped.Where(r => r.Kind is ComponentKind.Php or ComponentKind.Xdebug),
            r => Assert.Contains("nts", r.Url.AbsoluteUri, StringComparison.Ordinal));
    }

    private sealed class FixtureHandler(string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
