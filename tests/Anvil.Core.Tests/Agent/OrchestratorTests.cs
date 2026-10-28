using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Anvil.Agent;
using Anvil.Core.Agent;
using Anvil.Core.Apache;
using Anvil.Core.Components;
using Anvil.Core.Elevation;
using Anvil.Core.IO;
using Anvil.Core.Paths;
using Anvil.Core.Php;
using Anvil.Core.Preflight;
using Anvil.Core.Processes;
using Anvil.Core.Settings;
using Anvil.Core.Sites;

namespace Anvil.Core.Tests.Agent;

public sealed class OrchestratorTests : IDisposable
{
    private const string Version = "2.4.69";
    private const string StockConf = "Define SRVROOT \"c:/Apache24\"\r\nServerRoot \"${SRVROOT}\"\r\nListen 80\r\nLoadModule authz_core_module modules/mod_authz_core.so\r\n";

    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ap-orch-" + Guid.NewGuid().ToString("N")));
    private readonly FakeTester _tester = new();
    private readonly List<string> _events = [];
    private readonly FakeApache _apache;
    private readonly FakePools _pools;
    private IReadOnlyList<PreflightFinding> _findings = [];

    public OrchestratorTests()
    {
        _apache = new FakeApache(_events);
        _pools = new FakePools(_events);
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

    private Orchestrator Make(HttpMessageHandler? downloads = null, Func<int, CancellationToken, Task<bool>>? phpProbe = null) =>
        new(_paths, new SettingsStore(_paths), _tester, _apache, _ => _findings,
            new ComponentCatalog([], new ShippedCatalogProvider(_paths)),
            new ComponentInstaller(_paths, new Downloader(new HttpClient(downloads ?? new HttpClientHandler())), _ => null), _pools, _ => true,
            phpProbe: phpProbe);

    private void InstallPhp(string version, string toolset = "vs17") => TestPhp.Install(_paths, version, toolset);

    private void UsePhp(string defaultPhp)
    {
        var store = new SettingsStore(_paths);
        store.Save(store.Load() with { DefaultPhp = defaultPhp });
    }

    private string Vhost(string name) => File.ReadAllText(Path.Combine(_paths.Vhosts, name));

    private string AnvilConf => File.ReadAllText(Path.Combine(_paths.Conf, "anvil.conf"));

    [Fact]
    public async Task Moved_root_regenerates_everything()
    {
        // A folder copied from E:\Old: the marker, the user's httpd.conf and the generated files all name the old place.
        const string old = "E:/Old";
        File.WriteAllText(Path.Combine(_paths.Data, ".root"), @"E:\Old");
        Directory.CreateDirectory(_paths.ApacheConfDir);
        File.WriteAllText(_paths.HttpdConf, StockConf.Replace("c:/Apache24", $"{old}/runtime/apache/{Version}")
            + $"# >>> Anvil >>>\r\nInclude \"{old}/data/conf/anvil.conf\"\r\n# <<< Anvil <<<\r\n");
        Directory.CreateDirectory(_paths.Conf);
        File.WriteAllText(Path.Combine(_paths.Conf, "anvil.conf"), $"ErrorLog \"{old}/data/logs/apache/error.log\"\r\n");

        await Make().InitializeAsync(TestContext.Current.CancellationToken);

        var fwd = AppPaths.ForApache(_paths.Root);
        var httpd = File.ReadAllText(_paths.HttpdConf);
        Assert.Contains($"Define SRVROOT \"{fwd}/runtime/apache/{Version}\"", httpd);
        Assert.Contains($"Include \"{fwd}/data/conf/anvil.conf\"", httpd);
        Assert.DoesNotContain(old, httpd);
        Assert.Contains($"{fwd}/data/logs/apache/error.%Y-%m-%d.log", AnvilConf);
        Assert.DoesNotContain(old, AnvilConf);
        Assert.False(_paths.RootChanged());
    }

    [Fact]
    public async Task Moved_root_repoints_sites_and_folders_inside_it()
    {
        File.WriteAllText(Path.Combine(_paths.Data, ".root"), @"E:\Old");
        var sites = new SiteStore(_paths);
        sites.Save(new SiteDefinition
        {
            Id = "shop.test", Kind = SiteKind.Domain, Type = SiteType.Static, Name = "shop.test", Root = @"E:\Old\htdocs\shop",
        });
        sites.Save(new SiteDefinition { Id = "demo.test", Kind = SiteKind.Domain, Type = SiteType.Static, Name = "demo.test", Root = @"e:\old\demo" });
        sites.Save(new SiteDefinition { Id = "other.test", Kind = SiteKind.Domain, Type = SiteType.Static, Name = "other.test", Root = @"E:\Older\other" });
        var store = new SettingsStore(_paths);
        store.Save(store.Load() with { HtdocsPath = @"E:\Old\www" });

        await Make().InitializeAsync(TestContext.Current.CancellationToken);

        var shop = sites.All().Single(s => s.Id == "shop.test");
        Assert.Equal(Path.Combine(_paths.Root, "htdocs", "shop"), shop.Root);
        Assert.Equal(Path.Combine(_paths.Root, "demo"), sites.All().Single(s => s.Id == "demo.test").Root);
        Assert.Equal(@"E:\Older\other", sites.All().Single(s => s.Id == "other.test").Root);
        Assert.Equal(Path.Combine(_paths.Root, "www"), store.Load().HtdocsPath);
    }

    [Fact]
    public async Task Apply_failure_keeps_apache_running()
    {
        var ct = TestContext.Current.CancellationToken;
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);
        Assert.True((await orchestrator.StartAsync(ServiceId.Apache, ct)).Ok);
        var before = AnvilConf;
        var settings = orchestrator.Settings;
        _tester.Next = new ConfigTestResult(false, "AH00526: Syntax error on line 12 of httpd.conf");

        var result = await orchestrator.SaveSettingsAsync(settings with { Apache = settings.Apache with { ThreadsPerChild = 999 } }, ct);

        Assert.False(result.Ok);
        Assert.Contains("AH00526", result.ConfigTestOutput);
        Assert.NotNull(result.Error);
        Assert.Equal(0, _apache.Restarts);
        Assert.Equal(0, _apache.Stops);
        Assert.Equal(ProcessState.Running, _apache.Status?.State);
        Assert.Equal(before, AnvilConf);
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
        Assert.Contains("anvil.conf", result.ChangedFiles);
        Assert.Contains("ThreadsPerChild 999", AnvilConf);
        Assert.Equal(1, _apache.Restarts);
        Assert.Equal(999, new SettingsStore(_paths).Load().Apache.ThreadsPerChild);
    }

    [Fact]
    public async Task Port_change_to_a_busy_port_is_refused_before_anything_stops()
    {
        var ct = TestContext.Current.CancellationToken;
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);
        await orchestrator.StartAsync(ServiceId.Apache, ct);
        var settings = orchestrator.Settings;
        _findings = [new PreflightFinding("port-8080", Severity.Blocking, "Port 8080 is used by node (PID 7).", "Close node, or change the HTTP port in Settings.")];

        var result = await orchestrator.SaveSettingsAsync(settings with { HttpPort = 8080 }, ct);

        Assert.Equal("Port 8080 is used by node (PID 7). Close node, or change the HTTP port in Settings. The settings were not saved.", result.Error);
        Assert.Equal(0, _apache.Stops);
        Assert.Equal(ProcessState.Running, _apache.Status?.State);
        Assert.Equal(80, new SettingsStore(_paths).Load().HttpPort);
        Assert.Equal(settings, orchestrator.Settings);
    }

    [Fact]
    public async Task Port_apache_cannot_serve_goes_back_to_the_previous_settings()
    {
        var ct = TestContext.Current.CancellationToken;
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);
        await orchestrator.StartAsync(ServiceId.Apache, ct);
        var settings = orchestrator.Settings;
        _apache.BusyPorts.Add(8080);

        var result = await orchestrator.SaveSettingsAsync(settings with { HttpPort = 8080 }, ct);

        Assert.Equal("Apache did not answer on port 8080 within 15 s. Anvil went back to the previous settings, and Apache runs as before.",
            result.Error);
        Assert.Equal((Version, 80), _apache.StartedWith);
        Assert.Equal(ProcessState.Running, _apache.Status?.State);
        Assert.Equal(80, new SettingsStore(_paths).Load().HttpPort);
        Assert.Equal(settings, orchestrator.Settings);
        Assert.DoesNotContain("8080", AnvilConf);
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
        Assert.Equal("port-80", Assert.Single(orchestrator.Status().Findings, f => f.Severity == Severity.Blocking).Id);
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

    [Fact]
    public async Task Start_runs_the_default_php_pool_before_apache()
    {
        var ct = TestContext.Current.CancellationToken;
        InstallPhp("8.5.12");
        UsePhp("8.5.12");
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);

        var result = await orchestrator.StartAsync(null, ct);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(["pools php85", "apache start"], _events);
        var pool = Assert.Single(_pools.Running);
        Assert.Equal("8.5.12", pool.Version);
        Assert.Contains($"SetHandler \"proxy:fcgi://127.0.0.1:{pool.Port}/\"", Vhost("01-localhost.conf"));
        Assert.Equal(pool.Port, new SettingsStore(_paths).Load().PhpPoolPorts["php85"]);
        Assert.True(File.Exists(Path.Combine(_paths.PhpConfigDir("8.5.12"), "php.ini")));
        Assert.Equal(ProcessState.Running, orchestrator.Status().Services.Single(s => s.Id == ServiceId.Php).State);
    }

    [Fact]
    public async Task Start_writes_development_values_into_php_ini_and_defaults_conf()
    {
        var ct = TestContext.Current.CancellationToken;
        InstallPhp("8.5.12");
        UsePhp("8.5.12");
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);

        var result = await orchestrator.StartAsync(null, ct);

        Assert.True(result.Ok, result.Error);
        var ini = File.ReadAllText(Path.Combine(_paths.PhpConfigDir("8.5.12"), "php.ini"));
        Assert.Contains("memory_limit = 1G", ini, StringComparison.Ordinal);
        Assert.Contains("display_errors = On", ini, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_paths.PhpConfigDir("8.5.12"), "conf.d", "99-anvil-mode.ini")));
        Assert.Contains("Timeout 300", File.ReadAllText(Path.Combine(_paths.Conf, "defaults.conf")), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_paths.Conf, "mode.conf")));
    }

    [Fact]
    public async Task Stop_stops_the_pools_after_apache()
    {
        var ct = TestContext.Current.CancellationToken;
        InstallPhp("8.5.12");
        UsePhp("8.5.12");
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);
        await orchestrator.StartAsync(null, ct);
        _events.Clear();

        await orchestrator.StopAsync(null, ct);

        Assert.Equal(["apache stop", "pools"], _events);
        Assert.Empty(_pools.Running);
    }

    [Fact]
    public async Task Php_versions_report_default_end_of_life_and_pool()
    {
        var ct = TestContext.Current.CancellationToken;
        InstallPhp("8.5.12");
        InstallPhp("7.4.33", "vc15");
        UsePhp("8.5.12");
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);
        await orchestrator.StartAsync(null, ct);

        var php = orchestrator.GetPhp();

        Assert.Equal(["8.5.12", "7.4.33"], php.Select(p => p.Version));
        var current = php[0];
        Assert.True(current.IsDefault);
        Assert.False(current.EndOfLife);
        Assert.Equal("vs17", current.Toolset);
        Assert.Equal(["htdocs"], current.UsedBy);
        Assert.Equal(ProcessState.Running, current.State);
        Assert.Equal(_pools.Running.Single().Port, current.Port);
        Assert.Equal(Suggest.PhpWorkers(Environment.ProcessorCount), current.Workers);
        Assert.Equal(PhpPoolPlanner.DefaultMaxRequests, current.MaxRequests);
        var legacy = php[1];
        Assert.False(legacy.IsDefault);
        Assert.True(legacy.EndOfLife);
        Assert.Empty(legacy.UsedBy);
        Assert.Equal(ProcessState.Stopped, legacy.State);
    }

    [Fact]
    public async Task Pool_settings_are_saved_and_reshape_the_running_pool()
    {
        var ct = TestContext.Current.CancellationToken;
        InstallPhp("8.5.12");
        UsePhp("8.5.12");
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);
        await orchestrator.StartAsync(null, ct);

        var result = await orchestrator.SetPhpPoolAsync("8.5.12", new PhpPoolSettings(3, 50), ct);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(new PhpPoolSettings(3, 50), new SettingsStore(_paths).Load().PhpPools["8.5.12"]);
        var pool = Assert.Single(_pools.Running);
        Assert.Equal((3, 50), (pool.Workers, pool.MaxRequests));
    }

    [Theory]
    [InlineData(0, 1000, "Workers must be between 1 and 64.")]
    [InlineData(65, 1000, "Workers must be between 1 and 64.")]
    [InlineData(4, 0, "Recycle after must be between 1 and 1000000 requests.")]
    public async Task Pool_settings_out_of_range_are_refused(int workers, int maxRequests, string error)
    {
        var ct = TestContext.Current.CancellationToken;
        InstallPhp("8.5.12");
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);

        var result = await orchestrator.SetPhpPoolAsync("8.5.12", new PhpPoolSettings(workers, maxRequests), ct);

        Assert.False(result.Ok);
        Assert.Equal(error, result.Error);
        Assert.Empty(new SettingsStore(_paths).Load().PhpPools);
    }

    [Fact]
    public async Task New_default_php_moves_htdocs_and_reloads_apache()
    {
        var ct = TestContext.Current.CancellationToken;
        InstallPhp("8.5.12");
        InstallPhp("8.4.27");
        UsePhp("8.5.12");
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);
        await orchestrator.StartAsync(null, ct);

        var result = await orchestrator.SetDefaultPhpAsync("8.4.27", ct);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("8.4.27", new SettingsStore(_paths).Load().DefaultPhp);
        var pool = Assert.Single(_pools.Running);
        Assert.Equal("8.4.27", pool.Version);
        Assert.Contains($"SetHandler \"proxy:fcgi://127.0.0.1:{pool.Port}/\"", Vhost("01-localhost.conf"));
        Assert.Equal(1, _apache.Restarts);
    }

    [Fact]
    public async Task Rejected_default_php_keeps_the_old_one()
    {
        var ct = TestContext.Current.CancellationToken;
        InstallPhp("8.5.12");
        InstallPhp("8.4.27");
        UsePhp("8.5.12");
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);
        await orchestrator.StartAsync(null, ct);
        var before = Vhost("01-localhost.conf");
        _tester.Next = new ConfigTestResult(false, "AH00526: Syntax error");

        var result = await orchestrator.SetDefaultPhpAsync("8.4.27", ct);

        Assert.False(result.Ok);
        Assert.Equal("8.5.12", orchestrator.Settings.DefaultPhp);
        Assert.Equal("8.5.12", Assert.Single(_pools.Running).Version);
        Assert.Equal(before, Vhost("01-localhost.conf"));
        Assert.Equal(0, _apache.Restarts);
    }

    [Fact]
    public async Task Extension_change_restarts_only_its_pool()
    {
        var ct = TestContext.Current.CancellationToken;
        InstallPhp("8.5.12");
        UsePhp("8.5.12");
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);
        await orchestrator.StartAsync(null, ct);

        var result = await orchestrator.SetExtensionAsync("8.5.12", "intl", false, ct);

        Assert.True(result.Ok, result.Error);
        Assert.False(orchestrator.GetExtensions("8.5.12").Single(e => e.Name == "intl").Enabled);
        Assert.Equal(["php85"], _pools.Restarted);
        Assert.Equal(0, _apache.Restarts);
    }

    [Fact]
    public async Task Installing_the_first_php_adds_the_ca_bundle_and_makes_it_default()
    {
        var ct = TestContext.Current.CancellationToken;
        var ca = Encoding.ASCII.GetBytes("-----BEGIN CERTIFICATE-----");
        var zip = Zip("php-cgi.exe", "php.ini-development", "ext/php_intl.dll");
        Catalog($$"""{"kind":"CaBundle","version":"2026-10-20","url":"https://example.com/cacert.pem","sha256":"{{Sha(ca)}}"}""",
            $$"""{"kind":"Php","version":"8.5.12","url":"https://example.com/php.zip","sha256":"{{Sha(zip)}}","toolset":"vs17"}""");
        var downloads = new MapHandler(new() { ["/cacert.pem"] = ca, ["/php.zip"] = zip });
        var orchestrator = Make(downloads);
        await orchestrator.InitializeAsync(ct);

        await orchestrator.InstallAsync(new ComponentRelease(ComponentKind.Php, "8.5.12", new Uri("https://example.com/php.zip"), Sha(zip), "vs17"), ct);

        Assert.Equal("8.5.12", orchestrator.Settings.DefaultPhp);
        Assert.Equal("8.5.12", new SettingsStore(_paths).Load().DefaultPhp);
        Assert.Single(orchestrator.GetInstalled(ComponentKind.CaBundle));
        Assert.True(File.Exists(Path.Combine(_paths.PhpConfigDir("8.5.12"), "php.ini")));
        Assert.Contains("SetHandler", Vhost("01-localhost.conf"));
    }

    private static string Sha(byte[] body) => Convert.ToHexStringLower(SHA256.HashData(body));

    private static byte[] Zip(params string[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var name in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(name);
            }
        return ms.ToArray();
    }

    private void Catalog(params string[] releases)
    {
        Directory.CreateDirectory(_paths.Catalog);
        File.WriteAllText(Path.Combine(_paths.Catalog, "components.json"), $$"""{"schemaVersion":1,"releases":[{{string.Join(",", releases)}}]}""");
    }

    private sealed class MapHandler(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            return Task.FromResult(files.TryGetValue(request.RequestUri!.AbsolutePath, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task Installing_xdebug_turns_it_on_for_that_php_branch()
    {
        var ct = TestContext.Current.CancellationToken;
        InstallPhp("8.5.12");
        InstallPhp("8.4.27");
        var dll = "xdebug"u8.ToArray();
        Catalog($$"""{"kind":"Xdebug","version":"3.5.3","url":"https://example.com/php_xdebug-3.5.3-8.5-nts-vs17-x86_64.dll","sha256":"{{Sha(dll)}}","appliesTo":"8.5"}""");
        var orchestrator = Make(new MapHandler(new() { ["/php_xdebug-3.5.3-8.5-nts-vs17-x86_64.dll"] = dll }));
        await orchestrator.InitializeAsync(ct);

        await orchestrator.InstallAsync(new ComponentRelease(ComponentKind.Xdebug, "3.5.3",
            new Uri("https://example.com/php_xdebug-3.5.3-8.5-nts-vs17-x86_64.dll"), Sha(dll), AppliesTo: "8.5"), ct);

        Assert.True(orchestrator.GetExtensions("8.5.12").Single(e => e.Name == "xdebug").Enabled);
        Assert.DoesNotContain(orchestrator.GetExtensions("8.4.27"), e => e.Name == "xdebug" && e.Enabled);
        Assert.Contains("xdebug.start_with_request = trigger", File.ReadAllText(Path.Combine(_paths.PhpConfigDir("8.5.12"), "php.ini")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Firewall_request_follows_what_is_open_to_the_network()
    {
        var ct = TestContext.Current.CancellationToken;
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);

        Assert.Equal(new FirewallSetRequest(Version, [80, 443], false), orchestrator.FirewallRequest());

        Assert.True((await orchestrator.SaveSettingsAsync(orchestrator.Settings with { Network = new Core.Settings.NetworkAccess(false) }, ct)).Ok);
        Assert.Null(orchestrator.FirewallRequest());
    }

    [Fact]
    public async Task Saving_settings_normalizes_and_checks_the_network_domain()
    {
        var ct = TestContext.Current.CancellationToken;
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);

        Assert.True((await orchestrator.SaveSettingsAsync(orchestrator.Settings with { NetworkDomain = " Arbib.PC " }, ct)).Ok);
        Assert.Equal("arbib.pc", orchestrator.Settings.NetworkDomain);
        Assert.Equal("arbib.pc", new SettingsStore(_paths).Load().NetworkDomain);

        var bad = await orchestrator.SaveSettingsAsync(orchestrator.Settings with { NetworkDomain = "pc" }, ct);
        Assert.Equal("A network domain needs at least two parts, such as arbib.pc.", bad.Error);
        Assert.Equal("arbib.pc", orchestrator.Settings.NetworkDomain);

        Assert.True((await orchestrator.SaveSettingsAsync(orchestrator.Settings with { NetworkDomain = "  " }, ct)).Ok);
        Assert.Null(orchestrator.Settings.NetworkDomain);
    }

    [Fact]
    public async Task Unknown_extension_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        InstallPhp("8.5.12");
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);

        var result = await orchestrator.SetExtensionAsync("8.5.12", "imagick", true, ct);

        Assert.False(result.Ok);
        Assert.Contains("php_imagick.dll", result.Error);
    }

    private void InstallApache(string version)
    {
        var runtime = _paths.ApacheRuntime(version);
        Directory.CreateDirectory(Path.Combine(runtime, "conf"));
        Directory.CreateDirectory(Path.Combine(runtime, "bin"));
        File.WriteAllText(Path.Combine(runtime, "conf", "httpd.conf"), StockConf);
        File.WriteAllText(Path.Combine(runtime, "bin", "httpd.exe"), "");
    }

    [Fact]
    public async Task Unhealthy_apache_switches_back()
    {
        var ct = TestContext.Current.CancellationToken;
        InstallApache("2.4.70");
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);
        Assert.True((await orchestrator.StartAsync(ServiceId.Apache, ct)).Ok);
        _apache.Unhealthy.Add("2.4.70");

        var result = await orchestrator.ActivateAsync(ComponentKind.Apache, "2.4.70", ct);

        Assert.False(result.Ok);
        Assert.Equal("Apache 2.4.70 did not start: Apache did not answer on port 80 within 15 s. Apache 2.4.69 is running again.", result.Error);
        Assert.Equal(Version, orchestrator.Settings.ActiveApache);
        Assert.Equal(Version, new SettingsStore(_paths).Load().ActiveApache);
        Assert.Equal((Version, 80), _apache.StartedWith);
        Assert.Equal(ProcessState.Running, _apache.Status?.State);
        Assert.Contains($"runtime/apache/{Version}", File.ReadAllText(_paths.HttpdConf));
    }

    [Fact]
    public async Task Healthy_apache_update_stays()
    {
        var ct = TestContext.Current.CancellationToken;
        InstallApache("2.4.70");
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);
        Assert.True((await orchestrator.StartAsync(ServiceId.Apache, ct)).Ok);

        var result = await orchestrator.ActivateAsync(ComponentKind.Apache, "2.4.70", ct);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("2.4.70", new SettingsStore(_paths).Load().ActiveApache);
        Assert.Equal(("2.4.70", 80), _apache.StartedWith);
    }

    private SiteStore PhpSites()
    {
        foreach (var version in new[] { "8.4.10", "8.5.5", "8.5.12" }) InstallPhp(version);
        var store = new SettingsStore(_paths);
        store.Save(store.Load() with
        {
            DefaultPhp = "8.5.5",
            PhpPools = new Dictionary<string, PhpPoolSettings> { ["8.5.5"] = new(6, 500), ["8.4.10"] = new(2, 100) },
        });
        var root = Directory.CreateDirectory(Path.Combine(_paths.Root, "www")).FullName;
        var sites = new SiteStore(_paths);
        sites.Save(new SiteDefinition
        {
            Id = "shop.example.com", Kind = SiteKind.Domain, Type = SiteType.Php, Name = "shop.example.com", Root = root, PhpVersion = "8.5.5",
        });
        sites.Save(new SiteDefinition { Id = "old.example.com", Kind = SiteKind.Domain, Type = SiteType.Php, Name = "old.example.com", Root = root, PhpVersion = "8.4.10" });
        return sites;
    }

    [Fact]
    public async Task Php_update_moves_the_branch_to_the_new_patch()
    {
        var ct = TestContext.Current.CancellationToken;
        var sites = PhpSites();
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);

        var result = await orchestrator.ActivateAsync(ComponentKind.Php, "8.5.12", ct);

        Assert.True(result.Ok, result.Error);
        var settings = new SettingsStore(_paths).Load();
        Assert.Equal("8.5.12", settings.DefaultPhp);
        Assert.Equal(new PhpPoolSettings(6, 500), settings.PhpPools["8.5.12"]);
        Assert.False(settings.PhpPools.ContainsKey("8.5.5"));
        Assert.Equal(new PhpPoolSettings(2, 100), settings.PhpPools["8.4.10"]);
        var shop = sites.All().Single(s => s.Id == "shop.example.com");
        Assert.Equal("8.5.12", shop.PhpVersion);
        Assert.Equal("8.4.10", sites.All().Single(s => s.Id == "old.example.com").PhpVersion);
        Assert.DoesNotContain("8.5.5", Vhost("01-localhost.conf"));
    }

    [Fact]
    public async Task Php_update_whose_workers_do_not_start_switches_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var sites = PhpSites();
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);
        Assert.True((await orchestrator.StartAsync(ServiceId.Apache, ct)).Ok);
        var before = sites.All();
        _pools.Broken.Add("8.5.12");

        var result = await orchestrator.ActivateAsync(ComponentKind.Php, "8.5.12", ct);

        Assert.False(result.Ok);
        Assert.Equal("PHP 8.5.12 did not start: php-cgi 8.5.12 exited with code -1073741515. PHP 8.5.5 is running again.", result.Error);
        Assert.Equal("8.5.5", new SettingsStore(_paths).Load().DefaultPhp);
        Assert.Equal(before, sites.All(), SiteJson.Instance);
        Assert.Equal(["8.4.10", "8.5.5"], _pools.Running.Select(p => p.Version).Order());
    }

    [Fact]
    public async Task Php_update_that_does_not_answer_goes_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var sites = PhpSites();
        var probed = new List<int>();
        var orchestrator = Make(phpProbe: (port, _) =>
        {
            probed.Add(port);
            return Task.FromResult(false);
        });
        await orchestrator.InitializeAsync(ct);
        Assert.True((await orchestrator.StartAsync(ServiceId.Apache, ct)).Ok);
        var before = sites.All();

        var result = await orchestrator.ActivateAsync(ComponentKind.Php, "8.5.12", ct);

        Assert.False(result.Ok);
        Assert.Equal("PHP 8.5.12 started but did not answer a request, so Anvil went back to PHP 8.5.5.", result.Error);
        Assert.Equal("8.5.5", new SettingsStore(_paths).Load().DefaultPhp);
        Assert.Equal("8.5.5", orchestrator.GetPhp().Single(p => p.IsDefault).Version);
        Assert.Equal(before, sites.All(), SiteJson.Instance);
        Assert.Equal(["8.4.10", "8.5.5"], _pools.Running.Select(p => p.Version).Order());
        Assert.Single(probed);
    }

    [Fact]
    public async Task Php_update_runs_the_probe_script_on_the_new_pool()
    {
        var ct = TestContext.Current.CancellationToken;
        PhpSites();
        var probed = new List<int>();
        var orchestrator = Make(phpProbe: (port, _) =>
        {
            probed.Add(port);
            return Task.FromResult(true);
        });
        await orchestrator.InitializeAsync(ct);
        Assert.True((await orchestrator.StartAsync(ServiceId.Apache, ct)).Ok);

        var result = await orchestrator.ActivateAsync(ComponentKind.Php, "8.5.12", ct);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(_pools.Running.Single(p => p.Version == "8.5.12").Port, Assert.Single(probed));
        Assert.Equal(FastCgiProbe.Script, File.ReadAllText(Path.Combine(_paths.Run, FastCgiProbe.ScriptName)));
    }

    [Fact]
    public async Task Installing_a_patch_release_keeps_the_branch_php_ini()
    {
        var ct = TestContext.Current.CancellationToken;
        InstallPhp("8.5.5");
        new PhpIniBootstrap(_paths).EnsureIni("8.5.5");
        File.AppendAllText(Path.Combine(_paths.PhpConfigDir("8.5.5"), "php.ini"), "memory_limit = 2G\r\n");
        var ca = Encoding.ASCII.GetBytes("-----BEGIN CERTIFICATE-----");
        var zip = Zip("php-cgi.exe", "php.ini-development", "ext/php_intl.dll");
        Catalog($$"""{"kind":"CaBundle","version":"2026-10-20","url":"https://example.com/cacert.pem","sha256":"{{Sha(ca)}}"}""",
            $$"""{"kind":"Php","version":"8.5.12","url":"https://example.com/php.zip","sha256":"{{Sha(zip)}}","toolset":"vs17"}""");
        var orchestrator = Make(new MapHandler(new() { ["/cacert.pem"] = ca, ["/php.zip"] = zip }));
        await orchestrator.InitializeAsync(ct);

        await orchestrator.InstallAsync(new ComponentRelease(ComponentKind.Php, "8.5.12", new Uri("https://example.com/php.zip"), Sha(zip), "vs17"), ct);

        var ini = File.ReadAllText(Path.Combine(_paths.PhpConfigDir("8.5.12"), "php.ini"));
        Assert.Contains("memory_limit = 2G", ini);
        Assert.Contains(AppPaths.ForApache(Path.Combine(_paths.PhpRuntime("8.5.12"), "ext")), ini);
    }

    [Fact]
    public async Task Php_update_apache_rejects_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var sites = PhpSites();
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);
        var before = sites.All();
        _tester.Next = new ConfigTestResult(false, "AH00526: Syntax error");

        var result = await orchestrator.ActivateAsync(ComponentKind.Php, "8.5.12", ct);

        Assert.False(result.Ok);
        Assert.Contains("PHP 8.5.5 stays in use", result.Error);
        Assert.Equal("8.5.5", new SettingsStore(_paths).Load().DefaultPhp);
        Assert.Equal(before, sites.All(), SiteJson.Instance);
    }

    [Fact]
    public async Task Php_update_needs_a_newer_patch_of_a_branch_in_use()
    {
        var ct = TestContext.Current.CancellationToken;
        PhpSites();
        InstallPhp("8.6.0");
        var orchestrator = Make();
        await orchestrator.InitializeAsync(ct);

        Assert.Equal("PHP 8.6.0 is not a newer patch of a PHP version in use. Pick it for a site or as the default instead.",
            (await orchestrator.ActivateAsync(ComponentKind.Php, "8.6.0", ct)).Error);
        Assert.Equal("PHP 9.9.9 is not installed.", (await orchestrator.ActivateAsync(ComponentKind.Php, "9.9.9", ct)).Error);
    }

    // SiteDefinition holds lists, so records compare them by reference.
    private sealed class SiteJson : IEqualityComparer<SiteDefinition>
    {
        public static readonly SiteJson Instance = new();
        public bool Equals(SiteDefinition? x, SiteDefinition? y) => System.Text.Json.JsonSerializer.Serialize(x) == System.Text.Json.JsonSerializer.Serialize(y);
        public int GetHashCode(SiteDefinition obj) => 0;
    }

    [Fact]
    public async Task Install_uses_the_catalogs_url_and_hash_not_the_callers()
    {
        var ct = TestContext.Current.CancellationToken;
        InstallPhp("8.4.27"); // the CA bundle comes with the first PHP only
        var dll = "xdebug"u8.ToArray();
        Catalog($$"""{"kind":"Xdebug","version":"3.5.3","url":"https://example.com/php_xdebug.dll","sha256":"{{Sha(dll)}}","appliesTo":"8.4"}""");
        var downloads = new MapHandler(new() { ["/php_xdebug.dll"] = dll });
        var orchestrator = Make(downloads);
        await orchestrator.InitializeAsync(ct);

        await orchestrator.InstallAsync(new ComponentRelease(ComponentKind.Xdebug, "3.5.3", new Uri("https://evil.example/php_xdebug.dll"),
            new string('0', 64), AppliesTo: "8.4"), ct);

        Assert.Equal([new Uri("https://example.com/php_xdebug.dll")], downloads.Requested);
        Assert.Equal(Sha(dll), Assert.Single(orchestrator.GetInstalled(ComponentKind.Xdebug)).Sha256);
    }

    [Fact]
    public async Task Release_missing_from_the_catalog_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var downloads = new MapHandler([]);
        var orchestrator = Make(downloads);
        await orchestrator.InitializeAsync(ct);

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.InstallAsync(
            new ComponentRelease(ComponentKind.Php, "9.9.9", new Uri("https://example.com/php.zip"), new string('0', 64), "vs17"), ct));

        Assert.Equal("Php 9.9.9 is not in Anvil's catalog.", e.Message);
        Assert.Empty(downloads.Requested);
    }
}
