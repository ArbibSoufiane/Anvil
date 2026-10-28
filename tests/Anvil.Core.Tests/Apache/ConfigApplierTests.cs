using Anvil.Core.Apache;
using Anvil.Core.Paths;
using Anvil.Core.Settings;
using Anvil.Core.Sites;

namespace Anvil.Core.Tests.Apache;

public sealed class ConfigApplierTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ap-apply-" + Guid.NewGuid().ToString("N")));
    private readonly FakeTester _tester = new();

    public ConfigApplierTests() => _paths.EnsureCreated();

    public void Dispose() => Directory.Delete(_paths.Root, recursive: true);

    private ConfigApplier Applier() => new(new ConfigRenderer(_paths), new ConfigWriter(_paths), _tester);

    private static RenderInput Input(int port = 80, string? htdocs = null) => new()
    {
        Settings = new AppSettings { HttpPort = port, HtdocsPath = htdocs, Apache = new ApacheTuning(256, 0, 5) },
        Stock = new StockConfState(new HashSet<string>(), new HashSet<int>()),
    };

    private Dictionary<string, byte[]> Snapshot() =>
        Directory.GetFiles(_paths.Conf, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(_paths.Conf, f), File.ReadAllBytes);

    [Fact]
    public async Task First_apply_writes_all_files_and_tests_them()
    {
        var outcome = await Applier().ApplyAsync(Input(), TestContext.Current.CancellationToken);

        Assert.True(outcome.Ok);
        Assert.Equal(5, outcome.ChangedFiles.Count);
        Assert.True(File.Exists(Path.Combine(_paths.Vhosts, "00-catchall.conf")));
        Assert.Equal(1, _tester.Calls);
        Assert.True(Directory.Exists(Path.Combine(_paths.Logs, "apache")));
    }

    [Fact]
    public async Task Failed_test_restores_previous_conf_byte_identical()
    {
        await Applier().ApplyAsync(Input(), TestContext.Current.CancellationToken);
        var before = Snapshot();
        _tester.Next = new ConfigTestResult(false, "AH00526: Syntax error");

        var outcome = await Applier().ApplyAsync(Input(port: 8080), TestContext.Current.CancellationToken);

        Assert.False(outcome.Ok);
        Assert.Contains("AH00526", outcome.TestOutput);
        var after = Snapshot();
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (file, bytes) in before) Assert.Equal(bytes, after[file]);
        Assert.False(Directory.Exists(_paths.Conf + ".next"));
        Assert.False(Directory.Exists(_paths.Conf + ".previous"));
    }

    [Fact]
    public async Task Failed_first_apply_leaves_no_conf()
    {
        _tester.Next = new ConfigTestResult(false, "broken");

        var outcome = await Applier().ApplyAsync(Input(), TestContext.Current.CancellationToken);

        Assert.False(outcome.Ok);
        Assert.Empty(Directory.Exists(_paths.Conf) ? Directory.GetFiles(_paths.Conf, "*", SearchOption.AllDirectories) : []);
    }

    [Fact]
    public async Task Unchanged_render_reports_no_changes()
    {
        await Applier().ApplyAsync(Input(), TestContext.Current.CancellationToken);

        var outcome = await Applier().ApplyAsync(Input(), TestContext.Current.CancellationToken);

        Assert.True(outcome.Ok);
        Assert.Empty(outcome.ChangedFiles);
        Assert.Equal(1, _tester.Calls);
    }

    [Fact]
    public async Task Changed_file_listed()
    {
        await Applier().ApplyAsync(Input(), TestContext.Current.CancellationToken);

        var outcome = await Applier().ApplyAsync(Input(htdocs: @"E:\www"), TestContext.Current.CancellationToken);

        Assert.True(outcome.Ok);
        Assert.Contains(Path.Combine("vhosts", "01-localhost.conf"), outcome.ChangedFiles);
        Assert.DoesNotContain("anvil.conf", outcome.ChangedFiles);
        Assert.DoesNotContain("defaults.conf", outcome.ChangedFiles);
    }

    [Fact]
    public async Task Stale_generated_file_is_removed_and_listed()
    {
        await Applier().ApplyAsync(Input(), TestContext.Current.CancellationToken);
        File.WriteAllText(Path.Combine(_paths.Vhosts, "50-old-site.conf"), "# old");

        var outcome = await Applier().ApplyAsync(Input(), TestContext.Current.CancellationToken);

        Assert.Equal([Path.Combine("vhosts", "50-old-site.conf")], outcome.ChangedFiles);
        Assert.False(File.Exists(Path.Combine(_paths.Vhosts, "50-old-site.conf")));
    }

    [Fact]
    public async Task Site_log_folders_exist_before_the_test()
    {
        var site = new SiteDefinition
        {
            Id = "shop.test", Kind = SiteKind.Domain, Type = SiteType.Static, Name = "shop.test", Root = _paths.Htdocs,
        };

        var outcome = await Applier().ApplyAsync(Input() with { Sites = [site] }, TestContext.Current.CancellationToken);

        Assert.True(outcome.Ok);
        Assert.True(Directory.Exists(Path.Combine(_paths.Logs, "sites", "shop.test")));
    }

    private sealed class FakeTester : IConfigTester
    {
        public int Calls { get; private set; }
        public ConfigTestResult Next { get; set; } = new(true, "Syntax OK");

        public Task<ConfigTestResult> TestAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Next);
        }
    }
}
