using ArbiPache.Agent;
using ArbiPache.Core.Agent;
using ArbiPache.Core.Apache;
using ArbiPache.Core.Components;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Preflight;
using ArbiPache.Core.Processes;
using ArbiPache.Core.Settings;

namespace ArbiPache.Core.Tests.Agent;

public sealed class OrchestratorTests : IDisposable
{
    private const string Version = "2.4.69";
    private const string StockConf = "Define SRVROOT \"c:/Apache24\"\r\nServerRoot \"${SRVROOT}\"\r\nListen 80\r\nLoadModule authz_core_module modules/mod_authz_core.so\r\n";

    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ap-orch-" + Guid.NewGuid().ToString("N")));
    private readonly FakeTester _tester = new();
    private readonly FakeApache _apache = new();
    private IReadOnlyList<PreflightFinding> _findings = [];

    public OrchestratorTests()
    {
        var runtime = _paths.ApacheRuntime(Version);
        Directory.CreateDirectory(Path.Combine(runtime, "conf"));
        Directory.CreateDirectory(Path.Combine(runtime, "bin"));
        File.WriteAllText(Path.Combine(runtime, "conf", "httpd.conf"), StockConf);
        File.WriteAllText(Path.Combine(runtime, "bin", "httpd.exe"), "");
        Directory.CreateDirectory(_paths.Data);
        new SettingsStore(_paths).Save(new AppSettings { ActiveApache = Version, StartServicesOnLaunch = false });
    }

    public void Dispose()
    {
        try { Directory.Delete(_paths.Root, recursive: true); } catch (IOException) { }
    }

    private Orchestrator Make() => new(_paths, new SettingsStore(_paths), _tester, _apache, _ => _findings,
        new ComponentCatalog([], new ShippedCatalogProvider(_paths)),
        new ComponentInstaller(_paths, new Downloader(new HttpClient()), _ => null));

    private string ArbipacheConf => File.ReadAllText(Path.Combine(_paths.Conf, "arbipache.conf"));

    [Fact]
    public async Task Moved_root_regenerates_everything()
    {
        // A folder copied from E:\Old: the marker, the user's httpd.conf and the generated files all name the old place.
        const string old = "E:/Old";
        File.WriteAllText(Path.Combine(_paths.Data, ".root"), @"E:\Old");
        Directory.CreateDirectory(_paths.ApacheConfDir);
        File.WriteAllText(_paths.HttpdConf, StockConf.Replace("c:/Apache24", $"{old}/runtime/apache/{Version}")
            + $"# >>> ArbiPache >>>\r\nInclude \"{old}/data/conf/arbipache.conf\"\r\n# <<< ArbiPache <<<\r\n");
        Directory.CreateDirectory(_paths.Conf);
        File.WriteAllText(Path.Combine(_paths.Conf, "arbipache.conf"), $"ErrorLog \"{old}/data/logs/apache/error.log\"\r\n");

        await Make().InitializeAsync(TestContext.Current.CancellationToken);

        var fwd = AppPaths.ForApache(_paths.Root);
        var httpd = File.ReadAllText(_paths.HttpdConf);
        Assert.Contains($"Define SRVROOT \"{fwd}/runtime/apache/{Version}\"", httpd);
        Assert.Contains($"Include \"{fwd}/data/conf/arbipache.conf\"", httpd);
        Assert.DoesNotContain(old, httpd);
        Assert.Contains($"ErrorLog \"{fwd}/data/logs/apache/error.log\"", ArbipacheConf);
        Assert.DoesNotContain(old, ArbipacheConf);
        Assert.False(_paths.RootChanged());
    }

    [Fact]
    public async Task Apply_failure_keeps_apache_running()
    {
        var ct = TestContext.Current.CancellationToken;
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);
        Assert.True((await orchestrator.StartAsync(ServiceId.Apache, ct)).Ok);
        var before = ArbipacheConf;
        var settings = orchestrator.Settings;
        _tester.Next = new ConfigTestResult(false, "AH00526: Syntax error on line 12 of httpd.conf");

        var result = await orchestrator.SaveSettingsAsync(settings with { Apache = settings.Apache with { ThreadsPerChild = 999 } }, ct);

        Assert.False(result.Ok);
        Assert.Contains("AH00526", result.ConfigTestOutput);
        Assert.NotNull(result.Error);
        Assert.Equal(0, _apache.Restarts);
        Assert.Equal(0, _apache.Stops);
        Assert.Equal(ProcessState.Running, _apache.Status?.State);
        Assert.Equal(before, ArbipacheConf);
        Assert.Equal(settings.Apache.ThreadsPerChild, new SettingsStore(_paths).Load().Apache.ThreadsPerChild);
        Assert.Equal(settings, orchestrator.Settings);
    }

    [Fact]
    public async Task Saved_settings_reload_running_apache()
    {
        var ct = TestContext.Current.CancellationToken;
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);
        await orchestrator.StartAsync(ServiceId.Apache, ct);
        var settings = orchestrator.Settings;

        var result = await orchestrator.SaveSettingsAsync(settings with { Apache = settings.Apache with { ThreadsPerChild = 999 } }, ct);

        Assert.True(result.Ok, result.Error);
        Assert.Contains("arbipache.conf", result.ChangedFiles);
        Assert.Contains("ThreadsPerChild 999", ArbipacheConf);
        Assert.Equal(1, _apache.Restarts);
        Assert.Equal(999, new SettingsStore(_paths).Load().Apache.ThreadsPerChild);
    }

    [Fact]
    public async Task Launch_starts_apache_when_nothing_blocks()
    {
        new SettingsStore(_paths).Save(new AppSettings { ActiveApache = Version, StartServicesOnLaunch = true });

        await Make().InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProcessState.Running, _apache.Status?.State);
        Assert.Equal((Version, 80), _apache.StartedWith);
    }

    [Fact]
    public async Task Blocking_finding_prevents_launch_start()
    {
        new SettingsStore(_paths).Save(new AppSettings { ActiveApache = Version, StartServicesOnLaunch = true });
        _findings = [new PreflightFinding("port-80", Severity.Blocking, "Port 80 is used by Skype (PID 7).", "Close Skype.")];
        var orchestrator = Make();

        await orchestrator.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Null(_apache.Status);
        Assert.Equal("port-80", Assert.Single(orchestrator.Status().Findings).Id);
    }

    [Fact]
    public async Task Start_without_apache_installed_explains()
    {
        new SettingsStore(_paths).Save(new AppSettings { StartServicesOnLaunch = false });
        var orchestrator = Make();
        await orchestrator.InitializeAsync(TestContext.Current.CancellationToken);

        var result = await orchestrator.StartAsync(ServiceId.Apache, TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal("Install Apache first, on the Apache page.", result.Error);
    }

    private sealed class FakeTester : IConfigTester
    {
        public ConfigTestResult Next { get; set; } = new(true, "Syntax OK");
        public Task<ConfigTestResult> TestAsync(CancellationToken ct) => Task.FromResult(Next);
    }

    private sealed class FakeApache : IApacheControl
    {
        public ProcessStatus? Status { get; private set; }
        public (string Version, int Port)? StartedWith { get; private set; }
        public int Restarts { get; private set; }
        public int Stops { get; private set; }

        public event EventHandler<ProcessStatus>? StatusChanged;

        public Task StartAsync(string version, int httpPort, CancellationToken ct)
        {
            StartedWith = (version, httpPort);
            Set(ProcessState.Running);
            return Task.CompletedTask;
        }

        public Task GracefulRestartAsync(CancellationToken ct)
        {
            Restarts++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            Stops++;
            Set(ProcessState.Stopped);
            return Task.CompletedTask;
        }

        private void Set(ProcessState state)
        {
            Status = new ProcessStatus(ApacheControl.ProcessName, state, state == ProcessState.Running ? 42 : null, null, 0, null);
            StatusChanged?.Invoke(this, Status);
        }
    }
}
