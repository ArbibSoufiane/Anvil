using ArbiPache.Core.Apache;
using ArbiPache.Core.Components;
using ArbiPache.Core.Modes;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Settings;
using ArbiPache.Integration.Tests.Support;

namespace ArbiPache.Integration.Tests;

public sealed class ApacheConfigTests : IDisposable
{
    private readonly List<AppPaths> _roots = [];

    public void Dispose()
    {
        foreach (var root in _roots)
            if (Directory.Exists(root.Root)) Directory.Delete(root.Root, recursive: true);
    }

    private async Task<(AppPaths Paths, ApplyOutcome Outcome)> ApplyAsync(string rootName, ServerMode mode, Action<AppPaths>? beforeApply = null)
    {
        var paths = Runtimes.NewRoot(rootName);
        _roots.Add(paths);
        var ct = TestContext.Current.CancellationToken;
        var version = await Runtimes.InstallAsync(paths, ComponentKind.Apache, ct);
        new HttpdConfBootstrap(paths).EnsureUserConf(version);
        beforeApply?.Invoke(paths);

        var input = new RenderInput
        {
            Settings = new AppSettings { Mode = mode, ActiveApache = version },
            Stock = StockConfScanner.Scan(File.ReadAllText(paths.HttpdConf)),
            Profile = BuiltInProfiles.For(mode),
            ApacheVersion = version,
        };
        var applier = new ConfigApplier(new ConfigRenderer(paths), new ConfigWriter(paths), new ApacheConfigTester(paths, () => version));
        return (paths, await applier.ApplyAsync(input, ct));
    }

    [Fact]
    public async Task Real_httpd_accepts_rendered_development_config()
    {
        var (_, outcome) = await ApplyAsync("ap-it-dev", ServerMode.Development);

        Assert.True(outcome.Ok, outcome.TestOutput);
        Assert.Contains("Syntax OK", outcome.TestOutput);
    }

    [Fact]
    public async Task Real_httpd_accepts_rendered_production_config()
    {
        var (_, outcome) = await ApplyAsync("ap-it-prod", ServerMode.Production);

        Assert.True(outcome.Ok, outcome.TestOutput);
    }

    [Fact]
    public async Task Real_httpd_accepts_unicode_root()
    {
        var (_, outcome) = await ApplyAsync("ap it Mes Sites café", ServerMode.Development);

        Assert.True(outcome.Ok, outcome.TestOutput);
    }

    [Fact]
    public async Task Real_httpd_rejection_rolls_back()
    {
        var (paths, outcome) = await ApplyAsync("ap-it-bad", ServerMode.Development,
            p => File.AppendAllText(p.HttpdConf, "BogusDirective on\r\n"));

        Assert.False(outcome.Ok);
        Assert.Contains("BogusDirective", outcome.TestOutput);
        Assert.Empty(Directory.GetFiles(paths.Conf, "*", SearchOption.AllDirectories));
    }
}
