using Anvil.Core.Sites;

namespace Anvil.Core.Tests.Sites;

public sealed class LegacyImporterTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "ap-legacy-" + Guid.NewGuid().ToString("N"));

    public LegacyImporterTests() => Directory.CreateDirectory(Path.Combine(_home, "sites"));

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    // The schema ArbiPache 3 writes to %USERPROFILE%\.arbipache\sites\<domain>.json.
    private void V3Site(string domain, string type = "php", string? phpVersion = "php-8.4.23-Win32-vs17-x64", bool https = true,
        string aliases = "[]", string? proxyTarget = null, bool enabled = true) =>
        File.WriteAllText(Path.Combine(_home, "sites", domain + ".json"), $$"""
            {
              "domain": "{{domain}}",
              "type": "{{type}}",
              "root": "D:\\Projects\\Laravel\\{{domain}}\\public",
              "php_version": {{(phpVersion is null ? "null" : $"\"{phpVersion}\"")}},
              "index": [
                "index.php",
                "index.html"
              ],
              "https": {{(https ? "true" : "false")}},
              "http_redirect": false,
              "aliases": {{aliases}},
              "proxy_target": {{(proxyTarget is null ? "null" : $"\"{proxyTarget}\"")}},
              "enabled": {{(enabled ? "true" : "false")}},
              "created": "2026-10-25T14:19:16"
            }
            """);

    private ImportReport Import(IReadOnlyList<SiteDefinition>? existing, out IReadOnlyList<SiteDefinition> sites, params string[] installedPhp) =>
        LegacyImporter.Import(_home, existing ?? [], installedPhp, out sites);

    [Fact]
    public void Maps_v3_site_fields()
    {
        V3Site("afaq.test", aliases: """["www.afaq.test"]""");

        var report = Import(null, out var sites, "8.4.23");

        var site = Assert.Single(sites);
        Assert.Equal("afaq.test", site.Id);
        Assert.Equal(SiteKind.Domain, site.Kind);
        Assert.Equal(SiteType.Php, site.Type);
        Assert.Equal("afaq.test", site.Name);
        Assert.Equal(@"D:\Projects\Laravel\afaq.test\public", site.Root);
        Assert.Equal("8.4.23", site.PhpVersion);
        Assert.Equal(["www.afaq.test"], site.Aliases);
        Assert.Equal(["index.php", "index.html"], site.Index);
        Assert.Equal(CertSource.LocalDev, site.Cert);
        Assert.False(site.HttpRedirect);
        Assert.True(site.Enabled);
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 10, 25, 14, 19, 16, DateTimeKind.Local)), site.Created);
        Assert.Equal(["afaq.test"], report.Imported);
        Assert.Empty(report.Skipped);
        Assert.Empty(report.MissingPhpVersions);
    }

    [Fact]
    public void Php_version_name_parsed()
    {
        V3Site("a.test", phpVersion: "php-8.3.32-Win32-vs16-x64");
        V3Site("b.test", phpVersion: "php-7.4.9-Win32-vc15-x86");
        V3Site("c.test", phpVersion: null);

        Import(null, out var sites, "8.3.32", "7.4.9");

        Assert.Equal(["8.3.32", "7.4.9", null], sites.Select(s => s.PhpVersion));
    }

    [Fact]
    public void Existing_domain_skipped()
    {
        V3Site("shop.test");
        V3Site("blog.test");
        var existing = new SiteDefinition { Id = "shop.test", Kind = SiteKind.Domain, Type = SiteType.Php, Name = "shop.test", Root = @"D:\shop" };

        var report = Import([existing], out var sites, "8.4.23");

        Assert.Equal(["blog.test"], sites.Select(s => s.Id));
        Assert.Equal(["blog.test"], report.Imported);
        Assert.Equal(["shop.test: already exists"], report.Skipped);
    }

    [Fact]
    public void Domain_already_an_alias_is_skipped()
    {
        V3Site("www.shop.test");
        var existing = new SiteDefinition
        {
            Id = "shop.test", Kind = SiteKind.Domain, Type = SiteType.Php, Name = "shop.test", Root = @"D:\shop", Aliases = ["www.shop.test"],
        };

        var report = Import([existing], out var sites, "8.4.23");

        Assert.Empty(sites);
        Assert.Equal(["www.shop.test: already exists"], report.Skipped);
    }

    [Fact]
    public void Missing_php_reported()
    {
        V3Site("old.test", phpVersion: "php-7.4.9-Win32-vc15-x64");
        V3Site("older.test", phpVersion: "php-7.4.9-Win32-vc15-x64");

        var report = Import(null, out var sites, "8.4.23");

        Assert.Equal(["7.4.9"], report.MissingPhpVersions);
        Assert.Equal(["old.test", "older.test"], report.Imported);
        Assert.All(sites, s =>
        {
            Assert.Equal("7.4.9", s.PhpVersion);
            Assert.False(s.Enabled);
        });
    }

    [Fact]
    public void Proxy_site_keeps_its_target_and_plain_http()
    {
        V3Site("app.test", type: "proxy", phpVersion: null, https: false, proxyTarget: "http://127.0.0.1:5173");

        Import(null, out var sites);

        var site = Assert.Single(sites);
        Assert.Equal(SiteType.Proxy, site.Type);
        Assert.Equal("http://127.0.0.1:5173", site.Root);
        Assert.Equal(CertSource.None, site.Cert);
    }

    [Fact]
    public void Unreadable_and_invalid_files_are_skipped()
    {
        File.WriteAllText(Path.Combine(_home, "sites", "broken.test.json"), "{ not json");
        V3Site("odd.test", type: "cgi");
        V3Site("good.test");

        var report = Import(null, out var sites, "8.4.23");

        Assert.Equal(["good.test"], sites.Select(s => s.Id));
        Assert.Equal(["broken.test.json: unreadable", "odd.test: unknown site type cgi"], report.Skipped);
    }

    [Fact]
    public void Site_failing_validation_is_skipped()
    {
        V3Site("app.test", type: "proxy", phpVersion: null, proxyTarget: null);

        var report = Import(null, out var sites);

        Assert.Empty(sites);
        Assert.StartsWith("app.test: ", Assert.Single(report.Skipped));
    }

    [Fact]
    public void Missing_home_imports_nothing()
    {
        var report = LegacyImporter.Import(Path.Combine(_home, "absent"), [], [], out var sites);

        Assert.Empty(sites);
        Assert.Empty(report.Imported);
        Assert.Empty(report.Skipped);
    }
}
