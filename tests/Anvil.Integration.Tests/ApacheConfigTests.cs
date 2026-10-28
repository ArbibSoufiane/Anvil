using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Anvil.Core.Apache;
using Anvil.Core.Certificates;
using Anvil.Core.Components;
using Anvil.Core.Paths;
using Anvil.Core.Php;
using Anvil.Core.Settings;
using Anvil.Core.Sites;
using Anvil.Integration.Tests.Support;

namespace Anvil.Integration.Tests;

public sealed class ApacheConfigTests : IDisposable
{
    private readonly List<AppPaths> _roots = [];

    public void Dispose()
    {
        foreach (var root in _roots)
            if (Directory.Exists(root.Root)) Directory.Delete(root.Root, recursive: true);
    }

    private async Task<(AppPaths Paths, ApplyOutcome Outcome)> ApplyAsync(
        string rootName, Action<AppPaths>? beforeApply = null, Func<AppPaths, RenderInput, RenderInput>? shape = null)
    {
        var paths = Runtimes.NewRoot(rootName);
        _roots.Add(paths);
        var ct = TestContext.Current.CancellationToken;
        var version = await Runtimes.InstallAsync(paths, ComponentKind.Apache, ct);
        new HttpdConfBootstrap(paths).EnsureUserConf(version);
        beforeApply?.Invoke(paths);

        var input = new RenderInput
        {
            Settings = new AppSettings { ActiveApache = version },
            Stock = StockConfScanner.Scan(File.ReadAllText(paths.HttpdConf)),
            ApacheVersion = version,
        };
        if (shape is not null) input = shape(paths, input);
        var applier = new ConfigApplier(new ConfigRenderer(paths), new ConfigWriter(paths), new ApacheConfigTester(paths, () => version));
        return (paths, await applier.ApplyAsync(input, ct));
    }

    [Fact]
    public async Task Real_httpd_accepts_rendered_development_config()
    {
        var (_, outcome) = await ApplyAsync("ap-it-dev");

        Assert.True(outcome.Ok, outcome.TestOutput);
        Assert.Contains("Syntax OK", outcome.TestOutput);
    }

    [Fact]
    public async Task Real_httpd_accepts_unicode_root()
    {
        var (_, outcome) = await ApplyAsync("ap it Mes Sites café");

        Assert.True(outcome.Ok, outcome.TestOutput);
    }

    [Fact]
    public async Task Real_httpd_accepts_every_site_kind()
    {
        var (_, outcome) = await ApplyAsync("ap-it-sites", shape: (paths, input) =>
        {
            string Folder(string name)
            {
                var dir = Path.Combine(paths.Root, "Mes Sites", name);
                Directory.CreateDirectory(dir);
                return dir;
            }

            SiteDefinition Site(SiteKind kind, string name, string root) =>
                new() { Id = SiteSet.IdFor(kind, name), Kind = kind, Type = SiteType.Php, Name = name, Root = root };

            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=shop.example.com", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(90));
            var certs = new CertificateStore(paths);
            certs.Save("shop.example.com", cert.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem(), cert.ExportCertificatePem(), CertSource.LocalDev);
            new LocalCa(paths, certs, TimeProvider.System).Issue("dev.test", ["dev.test"]);

            SiteDefinition[] sites =
            [
                Site(SiteKind.Domain, "shop.example.com", Folder("café shop")) with
                {
                    Cert = CertSource.LocalDev, Aliases = ["www.shop.example.com"],
                },
                Site(SiteKind.Domain, "kiosk.test", Folder("démo")) with { PhpVersion = "8.4.27" },
                Site(SiteKind.Domain, "app.test", "http://127.0.0.1:5173") with { Type = SiteType.Proxy, Access = AccessLevel.ThisPc },
                Site(SiteKind.Domain, "static.test", Folder("static")) with { Type = SiteType.Static, Index = ["index.html"] },
                Site(SiteKind.Domain, "dev.test", Folder("dev")) with { Cert = CertSource.LocalDev },
                Site(SiteKind.Htdocs, "legacy", Path.Combine(paths.Htdocs, "legacy")) with
                {
                    PhpVersion = "8.4.27", Access = AccessLevel.ThisPc,
                },
            ];
            Directory.CreateDirectory(Path.Combine(paths.Htdocs, "legacy"));
            HtdocsEntry[] htdocs =
            [
                new("legacy", Path.Combine(paths.Htdocs, "legacy"), false, "8.4.27", AccessLevel.ThisPc),
                new("pma", Folder("phpMyAdmin"), true, null, AccessLevel.ThisPc),
            ];
            PhpPool Pool(string version, int port) => new(version, PhpVersionKey.Of(version), port, 2, 100, "php-cgi.exe", paths.Data, paths.Data);

            return input with
            {
                Settings = input.Settings with { DefaultPhp = "8.5.12" },
                Pools = [Pool("8.5.12", 9100), Pool("8.4.27", 9101)],
                Sites = sites,
                Htdocs = htdocs,
                Certificates = certs.All(),
            };
        });

        Assert.True(outcome.Ok, outcome.TestOutput);
        Assert.Contains("Syntax OK", outcome.TestOutput);
    }

    [Fact]
    public async Task Network_names_pass_the_config_test()
    {
        var (_, outcome) = await ApplyAsync("ap-it-net", shape: (paths, input) =>
        {
            var shop = Path.Combine(paths.Root, "Mes Sites", "café shop");
            var blog = Path.Combine(paths.Htdocs, "blog");
            Directory.CreateDirectory(shop);
            Directory.CreateDirectory(blog);
            new LocalCa(paths, new CertificateStore(paths), TimeProvider.System).Issue("shop.test", ["shop.test"]);
            PhpPool Pool(string version, int port) => new(version, PhpVersionKey.Of(version), port, 2, 100, "php-cgi.exe", paths.Data, paths.Data);
            return input with
            {
                Settings = input.Settings with { DefaultPhp = "8.5.12", NetworkDomain = "arbib.pc" },
                Pools = [Pool("8.5.12", 9100), Pool("8.4.27", 9101)],
                Sites =
                [
                    new SiteDefinition
                    {
                        Id = "shop.test", Kind = SiteKind.Domain, Type = SiteType.Php, Name = "shop.test", Root = shop,
                        Cert = CertSource.LocalDev, Aliases = ["www.shop.test"],
                    },
                    new SiteDefinition
                    {
                        Id = "app.test", Kind = SiteKind.Domain, Type = SiteType.Proxy, Name = "app.test", Root = "http://127.0.0.1:5173",
                    },
                ],
                Htdocs = [new("blog", blog, false, "8.4.27", AccessLevel.Network)],
                Certificates = new CertificateStore(paths).All(),
            };
        });

        Assert.True(outcome.Ok, outcome.TestOutput);
        Assert.Contains(Path.Combine("vhosts", "net-shop.conf"), outcome.ChangedFiles);
        Assert.Contains(Path.Combine("vhosts", "net-app.conf"), outcome.ChangedFiles);
        Assert.Contains(Path.Combine("vhosts", "net-blog.conf"), outcome.ChangedFiles);
    }

    [Fact]
    public async Task Real_httpd_rejection_rolls_back()
    {
        var (paths, outcome) = await ApplyAsync("ap-it-bad",
            p => File.AppendAllText(p.HttpdConf, "BogusDirective on\r\n"));

        Assert.False(outcome.Ok);
        Assert.Contains("BogusDirective", outcome.TestOutput);
        Assert.Empty(Directory.GetFiles(paths.Conf, "*", SearchOption.AllDirectories));
    }
}
