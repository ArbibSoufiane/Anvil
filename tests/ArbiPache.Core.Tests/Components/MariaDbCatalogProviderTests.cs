using System.Net;
using ArbiPache.Core.Components;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace ArbiPache.Core.Tests.Components;

public sealed class MariaDbCatalogProviderTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Components", "Fixtures");

    // Responses saved from downloads.mariadb.org/rest-api on 2026-10-27.
    private static async Task<(IReadOnlyList<ComponentRelease> Releases, FixtureHandler Handler)> LoadAsync()
    {
        var handler = new FixtureHandler();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
        var provider = new MariaDbCatalogProvider(new HttpClient(handler), time);
        return (await provider.GetAsync(TestContext.Current.CancellationToken), handler);
    }

    [Fact]
    public async Task Picks_winx64_zip_with_sha256()
    {
        var (releases, _) = await LoadAsync();

        var mariadb118 = Assert.Single(releases, r => r.Version.StartsWith("11.8.", StringComparison.Ordinal));
        Assert.Equal(ComponentKind.MariaDb, mariadb118.Kind);
        Assert.Equal("11.8.9", mariadb118.Version);
        Assert.Equal(new Uri("https://downloads.mariadb.org/rest-api/mariadb/11.8.9/mariadb-11.8.9-winx64.zip"), mariadb118.Url);
        Assert.Equal("830c46727d9278eae212ae3eca44eeb9e71b2a68704e95f344a64fba7b1963f5", mariadb118.Sha256);
        Assert.Equal("mariadb-11.8.9-winx64", mariadb118.StripRoot);
    }

    [Fact]
    public async Task Marks_lts()
    {
        var (releases, _) = await LoadAsync();

        var lts = releases.ToDictionary(r => r.Version, r => r.Lts);
        Assert.True(lts["12.3.3"]);
        Assert.True(lts["11.8.9"]);
        Assert.True(lts["10.11.19"]);
        Assert.False(lts["13.0.2"]);
    }

    [Fact]
    public async Task Ignores_msi_and_debug_symbols()
    {
        var (releases, _) = await LoadAsync();

        Assert.All(releases, r => Assert.Matches(@"/mariadb-[\d.]+-winx64\.zip$", r.Url.AbsoluteUri));
    }

    [Fact]
    public async Task Only_stable_series_are_offered()
    {
        var (releases, handler) = await LoadAsync();

        Assert.Equal(["10.11.19", "10.6.28", "11.4.13", "11.8.9", "12.3.3", "13.0.2"], releases.Select(r => r.Version).Order());
        Assert.DoesNotContain(handler.Requested, u => u.AbsolutePath.Contains("/13.1/", StringComparison.Ordinal) ||
            u.AbsolutePath.Contains("/13.2/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Series_past_its_end_of_life_says_so()
    {
        var (releases, _) = await LoadAsync();

        Assert.Equal("End of life", releases.Single(r => r.Version == "10.6.28").Notes);
        Assert.All(releases.Where(r => r.Version != "10.6.28"), r => Assert.Null(r.Notes));
    }

    [Fact]
    public async Task Reads_the_official_api_with_an_honest_user_agent()
    {
        var (_, handler) = await LoadAsync();

        Assert.Equal(new Uri("https://downloads.mariadb.org/rest-api/mariadb/"), handler.Requested[0]);
        Assert.Contains(new Uri("https://downloads.mariadb.org/rest-api/mariadb/11.8/latest/"), handler.Requested);
        Assert.All(handler.Agents, a => Assert.StartsWith("ArbiPache/", a, StringComparison.Ordinal));
    }

    [Fact]
    public void Shipped_catalog_pins_mysql_84_lts()
    {
        var shipped = new ShippedCatalogProvider(new AppPaths(Repo.Path("."))).All();

        // Oracle has no release API, so MySQL comes from the shipped pin only.
        var mysql = Assert.Single(shipped, r => r.Kind == ComponentKind.MySql);
        Assert.StartsWith("8.4.", mysql.Version, StringComparison.Ordinal);
        Assert.Equal($"https://cdn.mysql.com/Downloads/MySQL-8.4/mysql-{mysql.Version}-winx64.zip", mysql.Url.AbsoluteUri);
        Assert.Equal($"mysql-{mysql.Version}-winx64", mysql.StripRoot);
        Assert.Matches("^[0-9a-f]{64}$", mysql.Sha256);
        Assert.True(mysql.Lts);
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];
        public List<string> Agents { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            Agents.Add(request.Headers.UserAgent.ToString());
            var path = request.RequestUri!.AbsolutePath;
            var file = path == "/rest-api/mariadb/" ? "mariadb-majors.json"
                : path.EndsWith("/latest/", StringComparison.Ordinal) ? $"mariadb-{path.Split('/')[^3]}-latest.json"
                : null;
            var full = file is null ? null : Path.Combine(Fixtures, file);
            return Task.FromResult(full is not null && File.Exists(full)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(File.ReadAllText(full)) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
