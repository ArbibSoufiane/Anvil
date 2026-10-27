using ArbiPache.Core.Sites;

namespace ArbiPache.Core.Tests.Sites;

public sealed class ProjectDetectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ap-detect-" + Guid.NewGuid().ToString("N"));

    public ProjectDetectorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private void Touch(string relative, string content = "")
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Laravel_serves_public()
    {
        Touch("artisan");
        Touch("composer.json", "{}");
        Touch(@"public\index.php");

        Assert.Equal(new Detected(SiteType.Php, Path.Combine(_dir, "public"), "Laravel", null), ProjectDetector.Detect(_dir));
    }

    [Fact]
    public void Symfony_serves_public()
    {
        Touch(@"bin\console");
        Touch("composer.json", "{}");
        Touch(@"public\index.php");

        Assert.Equal(new Detected(SiteType.Php, Path.Combine(_dir, "public"), "Symfony", null), ProjectDetector.Detect(_dir));
    }

    [Theory]
    [InlineData("wp-config.php")]
    [InlineData("wp-config-sample.php")]
    public void WordPress_serves_root(string marker)
    {
        Touch(marker);
        Touch("index.php");

        Assert.Equal(new Detected(SiteType.Php, _dir, "WordPress", null), ProjectDetector.Detect(_dir));
    }

    [Fact]
    public void Composer_project_serves_public_when_it_has_index()
    {
        Touch("composer.json", "{}");
        Assert.Equal(new Detected(SiteType.Php, _dir, "PHP", null), ProjectDetector.Detect(_dir));

        Touch(@"public\index.php");
        Assert.Equal(new Detected(SiteType.Php, Path.Combine(_dir, "public"), "PHP", null), ProjectDetector.Detect(_dir));
    }

    [Fact]
    public void Vite_becomes_proxy_5173()
    {
        Touch("package.json", """{ "scripts": { "dev": "vite" }, "devDependencies": { "vite": "^7.1.0" } }""");
        Touch("index.html");

        Assert.Equal(new Detected(SiteType.Proxy, _dir, "Vite", "http://127.0.0.1:5173"), ProjectDetector.Detect(_dir));
    }

    [Fact]
    public void Package_json_without_vite_is_not_a_proxy()
    {
        Touch("package.json", """{ "devDependencies": { "esbuild": "^0.25.0" } }""");
        Touch("index.html");

        Assert.Equal(SiteType.Static, ProjectDetector.Detect(_dir).Type);
    }

    [Fact]
    public void Broken_package_json_is_ignored()
    {
        Touch("package.json", "{ vite");

        Assert.Equal(new Detected(SiteType.Php, _dir, "PHP", null), ProjectDetector.Detect(_dir));
    }

    [Fact]
    public void Plain_html_is_static()
    {
        Touch("index.html");
        Touch(@"css\site.css");

        Assert.Equal(new Detected(SiteType.Static, _dir, "Static", null), ProjectDetector.Detect(_dir));
    }

    [Fact]
    public void Html_with_php_is_php()
    {
        Touch("index.html");
        Touch("contact.php");

        Assert.Equal(new Detected(SiteType.Php, _dir, "PHP", null), ProjectDetector.Detect(_dir));
    }

    [Fact]
    public void Empty_or_missing_folder_is_php()
    {
        Assert.Equal(new Detected(SiteType.Php, _dir, "PHP", null), ProjectDetector.Detect(_dir));
        Assert.Equal(SiteType.Php, ProjectDetector.Detect(Path.Combine(_dir, "missing")).Type);
    }
}
