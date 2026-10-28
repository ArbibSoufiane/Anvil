using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Anvil.Agent;
using Anvil.Core.Agent;
using Anvil.Core.Apache;
using Anvil.Core.Certificates;
using Anvil.Core.Components;
using Anvil.Core.Database;
using Anvil.Core.Elevation;
using Anvil.Core.Modes;
using Anvil.Core.Paths;
using Anvil.Core.Php;
using Anvil.Core.Preflight;
using Anvil.Core.Processes;
using Anvil.Core.Settings;
using Anvil.Core.Sites;

namespace Anvil.Core.Tests.Agent;

public sealed class RpcTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static string UniquePipe() => "Anvil.Agent.Test." + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Rpc_round_trip_over_pipe()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeApi();
        var name = UniquePipe();
        await using var server = new PipeServer(name, fake, []);
        server.Start();
        await using var client = await AgentRpc.ConnectAsync(name, Wait, ct);

        var status = await client.Api.GetStatusAsync(ct);
        var saved = await client.Api.SaveSettingsAsync(new AppSettings { HttpPort = 8080, Mode = ServerMode.Production }, ct);
        var started = await client.Api.StartAsync(null, ct);
        var test = await client.Api.TestConfigAsync(ct);

        Assert.Equal(ServerMode.Production, status.Mode);
        var apache = Assert.Single(status.Services);
        Assert.Equal(new ServiceStatus(ServiceId.Apache, ProcessState.Running, "2.4.69", null), apache);
        Assert.Equal(new PreflightFinding("port-443", Severity.Blocking, "Port 443 is used by Skype (PID 7).", "Close Skype."), Assert.Single(status.Findings));
        Assert.Equal("4.0.0-test", status.AgentVersion);
        Assert.True(saved.Ok);
        Assert.Equal(["anvil.conf"], saved.ChangedFiles);
        Assert.Equal(8080, fake.Saved?.HttpPort);
        Assert.Equal(ServerMode.Production, fake.Saved?.Mode);
        Assert.Equal([(ServiceId?)null], fake.Started);
        Assert.True(started.Ok);
        Assert.Equal(new ConfigTestResult(true, "Syntax OK"), test);
    }

    [Fact]
    public async Task Php_calls_round_trip_over_pipe()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeApi();
        var name = UniquePipe();
        await using var server = new PipeServer(name, fake, []);
        server.Start();
        await using var client = await AgentRpc.ConnectAsync(name, Wait, ct);

        var php = await client.Api.GetPhpAsync(ct);
        var extensions = await client.Api.GetExtensionsAsync("8.5.12", ct);
        var pool = await client.Api.SetPhpPoolAsync("8.5.12", new PhpPoolSettings(3, 50), ct);

        Assert.Equal(FakeApi.Php, Assert.Single(php) with { UsedBy = FakeApi.Php.UsedBy });
        Assert.Equal(["htdocs"], php[0].UsedBy);
        Assert.Equal(FakeApi.Intl, Assert.Single(extensions));
        Assert.True(pool.Ok);
        Assert.Equal(("8.5.12", new PhpPoolSettings(3, 50)), fake.Pool);
    }

    [Fact]
    public async Task Site_calls_round_trip_over_pipe()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeApi();
        var name = UniquePipe();
        await using var server = new PipeServer(name, fake, []);
        server.Start();
        await using var client = await AgentRpc.ConnectAsync(name, Wait, ct);

        var sites = await client.Api.GetSitesAsync(ct);
        var htdocs = await client.Api.GetHtdocsAsync(ct);
        var saved = await client.Api.SaveSiteAsync(FakeApi.Shop, new SiteSecrets("client", "pw"), ct);
        var detected = await client.Api.DetectAsync("D:/Sites/vite", ct);
        var imported = await client.Api.ImportLegacyAsync(ct);

        var site = Assert.Single(sites);
        Assert.Equal(JsonSerializer.Serialize(FakeApi.Shop), JsonSerializer.Serialize(site));
        Assert.Equal("blog", Assert.Single(htdocs).Folder);
        Assert.Equal(["shop.test"], saved.HostsDomains);
        Assert.Equal(new SiteSecrets("client", "pw"), fake.SavedSite!.Value.Secrets);
        Assert.Equal(new Detected(SiteType.Proxy, "D:/Sites/vite", "Vite", "http://127.0.0.1:5173"), detected);
        Assert.Equal(["afaq.test"], imported.Imported);
        Assert.Equal(["shop.test: already exists"], imported.Skipped);
        Assert.Equal(["7.4.9"], imported.MissingPhpVersions);
        Assert.Equal(["afaq.test"], imported.Result?.HostsDomains);
    }

    [Fact]
    public async Task Database_calls_round_trip_with_null_resets_and_messages()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeApi();
        var name = UniquePipe();
        await using var server = new PipeServer(name, fake, []);
        server.Start();
        await using var client = await AgentRpc.ConnectAsync(name, Wait, ct);

        var rows = await client.Api.GetDbSettingsAsync(ct);
        var saved = await client.Api.SaveDbSettingsAsync(new Dictionary<string, string?> { ["max_connections"] = null, ["wait_timeout"] = "600" }, ct);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => client.Api.BackupDatabaseAsync(ct));

        Assert.Equal(new DbSettingRow("max_connections", "300", "151", "Clients at once", true, false, false, false), Assert.Single(rows));
        Assert.True(saved.Ok);
        Assert.True(fake.DbChanges!.ContainsKey("max_connections"));
        Assert.Null(fake.DbChanges["max_connections"]);
        Assert.Equal("600", fake.DbChanges["wait_timeout"]);
        Assert.Contains("Start the database first.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mode_calls_round_trip_with_changes_and_options()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeApi();
        var name = UniquePipe();
        await using var server = new PipeServer(name, fake, []);
        server.Start();
        await using var client = await AgentRpc.ConnectAsync(name, Wait, ct);

        var preview = await client.Api.PreviewModeAsync(ServerMode.Production, ct);
        var applied = await client.Api.ApplyModeAsync(ServerMode.Production, new ModeOptions(KeepXdebug: true), ct);
        var profiles = await client.Api.GetModeProfilesAsync(ct);
        var saved = await client.Api.SaveModeProfileAsync(ServerMode.Production,
            new ModeProfileOverrides(new Dictionary<string, string> { ["memory_limit"] = "512M" }, null, null, null, true, null), ct);

        Assert.Equal((ServerMode.Development, ServerMode.Production), (preview.From, preview.To));
        Assert.Equal(new SettingChange("PHP", @"php\8.5.12\conf.d\99-anvil-mode.ini", "display_errors", "On", "Off"), Assert.Single(preview.Changes));
        Assert.Equal([ServiceId.Php, ServiceId.Apache], preview.Restarts);
        Assert.Equal(@"D:\Anvil\data\backups\2026-10-27-120000", preview.BackupPreviewPath);
        Assert.Equal(FirewallScope.Internet, applied.RequiredFirewall);
        Assert.Equal((ServerMode.Production, new ModeOptions(KeepXdebug: true)), fake.ModeApplied);
        Assert.Equal([ServerMode.Development, ServerMode.Production], profiles.Select(p => p.Mode));
        Assert.Equal(BuiltInProfiles.Production.Php.ToList(), profiles[1].Php.ToList());
        Assert.False(profiles[1].DirectoryListing);
        Assert.True(saved.Ok);
        Assert.Equal(ServerMode.Production, fake.ProfileSaved!.Value.Mode);
        Assert.Equal("512M", fake.ProfileSaved.Value.Overrides.Php!["memory_limit"]);
        Assert.Null(fake.ProfileSaved.Value.Overrides.Apache);
        Assert.True(fake.ProfileSaved.Value.Overrides.SecurityHeaders);
    }

    // Another Anvil folder, or another account's program, may own the pipe: the window talks only to its own Agent,
    // and sends it no passwords before it knows.
    [Fact]
    public async Task Connect_refuses_a_pipe_another_program_serves()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = UniquePipe();
        await using var server = new PipeServer(name, new FakeApi(), []);
        server.Start();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AgentRpc.ConnectAsync(name, Wait, ct, @"D:\Other\Anvil\Anvil.Agent.exe"));
        await using var own = await AgentRpc.ConnectAsync(name, Wait, ct, Environment.ProcessPath);

        Assert.Equal($"Another program answers on the Anvil Agent's pipe: {Environment.ProcessPath}. "
            + "If it is another copy of Anvil, close it, then open this one again.", error.Message);
        Assert.NotNull(own.Api);
    }

    // A portable copy may run from a junction or a subst drive: Windows names the Agent by its real path then.
    [Fact]
    public async Task Connect_accepts_its_agent_reached_through_a_junction()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = UniquePipe();
        await using var server = new PipeServer(name, new FakeApi(), []);
        server.Start();
        var junction = Path.Combine(Path.GetTempPath(), "ap-junction-" + Guid.NewGuid().ToString("N"));
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                   ["/c", "mklink", "/J", junction, Path.GetDirectoryName(Environment.ProcessPath)!]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
            await mklink.WaitForExitAsync(ct);
        try
        {
            await using var own = await AgentRpc.ConnectAsync(name, Wait, ct, Path.Combine(junction, Path.GetFileName(Environment.ProcessPath)!));

            Assert.NotNull(own.Api);
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    [Fact]
    public async Task Certificate_calls_round_trip_and_keep_their_error_code()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeApi();
        var name = UniquePipe();
        await using var server = new PipeServer(name, fake, []);
        server.Start();
        await using var client = await AgentRpc.ConnectAsync(name, Wait, ct);

        var issued = await client.Api.IssueLetsEncryptAsync("shop.test", staging: true, ct);
        var addresses = await client.Api.ResolveAsync("shop.example.com", ct);
        var certificates = await client.Api.GetCertificatesAsync(ct);
        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            client.Api.ImportCertificateAsync("shop.test", new CertImportRequest(new CertImportFile("client.pfx", [1, 2, 3]), "guess", null, null, null), ct));

        Assert.Equal(FakeApi.Cert, issued with { Names = FakeApi.Cert.Names });
        Assert.Equal(["shop.test"], issued.Names);
        Assert.Equal(("shop.test", true), fake.Issued);
        Assert.Equal(["203.0.113.5"], addresses);
        Assert.Equal(["shop.test"], certificates.Keys);
        Assert.Equal("bad-password", AgentErrors.CertificateCode(error));
        Assert.Equal("The password does not open this .pfx file.", error.Message);
        Assert.Null(AgentErrors.CertificateCode(new InvalidOperationException("other")));
    }

    [Fact]
    public async Task StatusChanged_reaches_client()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeApi();
        var name = UniquePipe();
        await using var server = new PipeServer(name, fake, []);
        server.Start();
        await using var client = await AgentRpc.ConnectAsync(name, Wait, ct);
        var received = new TaskCompletionSource<AgentStatus>();
        client.Api.StatusChanged += (_, s) => received.TrySetResult(s);
        await client.Api.GetStatusAsync(ct);

        fake.Raise(FakeApi.Status with { Mode = ServerMode.Development });

        var status = await received.Task.WaitAsync(Wait, ct);
        Assert.Equal(ServerMode.Development, status.Mode);
        Assert.Equal(ServiceId.Apache, Assert.Single(status.Services).Id);
    }

    [Fact]
    public void Pipe_acl_has_user_and_admins_not_everyone()
    {
        const string serviceSid = "S-1-5-80-1111111111-2222222222-3333333333-4444444444-555555555";

        var security = PipeServer.CreateSecurity([serviceSid]);

        var allowed = security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow)
            .Select(r => (SecurityIdentifier)r.IdentityReference)
            .ToHashSet();
        Assert.Contains(WindowsIdentity.GetCurrent().User!, allowed);
        Assert.Contains(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), allowed);
        Assert.Contains(new SecurityIdentifier(serviceSid), allowed);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.WorldSid, null), allowed);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), allowed);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), allowed);
        Assert.Equal(3, allowed.Count);
    }

    [Fact]
    public async Task Second_agent_exits_with_code_3()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new AgentOptions(UniquePipe(), @"Local\Anvil.Agent.Test." + Guid.NewGuid().ToString("N"), RunningAsService: false);
        var root = Path.Combine(Path.GetTempPath(), "ap-agent-" + Guid.NewGuid().ToString("N"));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var first = new AgentHost(options, new AppPaths(root)).RunAsync(stop.Token);
            await using (var client = await AgentRpc.ConnectAsync(options.PipeName, TimeSpan.FromSeconds(30), ct))
                await client.Api.GetStatusAsync(ct);

            var second = await new AgentHost(options, new AppPaths(root)).RunAsync(ct);

            Assert.Equal(AgentHost.ExitAlreadyRunning, second);
            Assert.Equal(3, second);
            stop.Cancel();
            Assert.Equal(0, await first.WaitAsync(TimeSpan.FromSeconds(30), ct));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class FakeApi : IAgentApi
    {
        public static readonly AgentStatus Status = new(ServerMode.Production,
            [new ServiceStatus(ServiceId.Apache, ProcessState.Running, "2.4.69", null)],
            [new PreflightFinding("port-443", Severity.Blocking, "Port 443 is used by Skype (PID 7).", "Close Skype.")],
            "4.0.0-test", RunningAsService: false);

        public static readonly PhpVersionInfo Php = new("8.5.12", "vs17", 4, 1000, 9100, ["htdocs"], ProcessState.Running, true, false);
        public static readonly PhpExtension Intl = new("intl", true, true, false, false);

        public AppSettings? Saved { get; private set; }
        public List<ServiceId?> Started { get; } = [];
        public (string, PhpPoolSettings)? Pool { get; private set; }

        public event EventHandler<AgentStatus>? StatusChanged;

        public void Raise(AgentStatus s) => StatusChanged?.Invoke(this, s);

        public Task<AgentStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(Status);

        public Task<ApplyResult> StartAsync(ServiceId? id, CancellationToken ct = default)
        {
            Started.Add(id);
            return Task.FromResult(new ApplyResult(true, null, null, []));
        }

        public Task<ApplyResult> StopAsync(ServiceId? id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApplyResult> RestartAsync(ServiceId? id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ConfigTestResult> TestConfigAsync(CancellationToken ct = default) => Task.FromResult(new ConfigTestResult(true, "Syntax OK"));
        public Task<AppSettings> GetSettingsAsync(CancellationToken ct = default) => Task.FromResult(new AppSettings());

        public Task<ApplyResult> SaveSettingsAsync(AppSettings s, CancellationToken ct = default)
        {
            Saved = s;
            return Task.FromResult(new ApplyResult(true, null, "Syntax OK", ["anvil.conf"]));
        }

        public Task<IReadOnlyList<ComponentRelease>> GetAvailableAsync(ComponentKind k, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<InstalledComponent>> GetInstalledAsync(ComponentKind k, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<InstalledComponent> InstallAsync(ComponentRelease r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApplyResult> ActivateAsync(ComponentKind k, string version, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<UpdateOffer>> CheckUpdatesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PhpVersionInfo>> GetPhpAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PhpVersionInfo>>([Php]);

        public Task<ApplyResult> SetPhpPoolAsync(string version, PhpPoolSettings settings, CancellationToken ct = default)
        {
            Pool = (version, settings);
            return Task.FromResult(ApplyResult.Success());
        }

        public Task<ApplyResult> SetDefaultPhpAsync(string version, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PhpExtension>> GetExtensionsAsync(string version, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PhpExtension>>([Intl]);
        public Task<ApplyResult> SetExtensionAsync(string version, string name, bool enabled, CancellationToken ct = default) => throw new NotSupportedException();
        public static readonly SiteDefinition Shop = new()
        {
            Id = "shop.test", Kind = SiteKind.Domain, Type = SiteType.Php, Name = "shop.test", Root = "D:/Sites/shop/public",
            Aliases = ["www.shop.test"], Mounts = [new SiteMount("/demo", "D:/Sites/demo", SiteType.Static, null)], Cert = CertSource.LocalDev,
            Created = new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero),
        };

        public (SiteDefinition Site, SiteSecrets? Secrets)? SavedSite { get; private set; }

        public Task<IReadOnlyList<SiteDefinition>> GetSitesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SiteDefinition>>([Shop]);
        public Task<IReadOnlyList<HtdocsEntry>> GetHtdocsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<HtdocsEntry>>([new HtdocsEntry("blog", "D:/htdocs/blog", false, null, AccessLevel.Network)]);
        public Task<IReadOnlyList<ValidationError>> ValidateSiteAsync(SiteDefinition s, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ApplyResult> SaveSiteAsync(SiteDefinition s, SiteSecrets? secrets, CancellationToken ct = default)
        {
            SavedSite = (s, secrets);
            return Task.FromResult(ApplyResult.Success() with { HostsDomains = ["shop.test"] });
        }

        public Task<ApplyResult> RemoveSiteAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApplyResult> SetSiteEnabledAsync(string id, bool enabled, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Detected> DetectAsync(string folder, CancellationToken ct = default) =>
            Task.FromResult(new Detected(SiteType.Proxy, folder, "Vite", "http://127.0.0.1:5173"));
        public Task<ApplyResult> CopyIntoHtdocsAsync(string sourceFolder, string folderName, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ImportReport> ImportLegacyAsync(CancellationToken ct = default) =>
            Task.FromResult(new ImportReport(["afaq.test"], ["shop.test: already exists"], ["7.4.9"]) { Result = ApplyResult.Success() with { HostsDomains = ["afaq.test"] } });

        public Task<ApplyResult> ReissueLocalCertificatesAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public static readonly SiteCertificate Cert = new(CertSource.LetsEncrypt, ["shop.test"], new DateTimeOffset(2027, 1, 25, 0, 0, 0, TimeSpan.Zero),
            "D:/Anvil/data/certs/shop.test/cert.pem", "D:/Anvil/data/certs/shop.test/key.pem", null);

        public (string Id, bool Staging)? Issued { get; private set; }

        public Task<SiteCertificate> ImportCertificateAsync(string siteId, CertImportRequest r, CancellationToken ct = default) =>
            AgentErrors.CertificateCall<SiteCertificate>(() =>
                throw new CertificateImportException("bad-password", "The password does not open this .pfx file."));

        public Task<SiteCertificate> IssueLetsEncryptAsync(string siteId, bool staging, CancellationToken ct = default)
        {
            Issued = (siteId, staging);
            return Task.FromResult(Cert);
        }

        public Task<IReadOnlyList<string>> ResolveAsync(string name, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(["203.0.113.5"]);

        public IReadOnlyDictionary<string, string?>? DbChanges { get; private set; }

        public Task<ApplyResult> InstallDatabaseAsync(DbEngine e, string version, string rootPassword, CancellationToken ct = default) =>
            Task.FromResult(ApplyResult.Success());

        public Task<IReadOnlyList<DbSettingRow>> GetDbSettingsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DbSettingRow>>([new DbSettingRow("max_connections", "300", "151", "Clients at once", true, false, false, false)]);

        public Task<ApplyResult> SaveDbSettingsAsync(IReadOnlyDictionary<string, string?> changes, CancellationToken ct = default)
        {
            DbChanges = changes;
            return Task.FromResult(ApplyResult.Success());
        }

        public Task<ApplyResult> SetDbRootPasswordAsync(string password, CancellationToken ct = default) => Task.FromResult(ApplyResult.Success());

        public Task<string> BackupDatabaseAsync(CancellationToken ct = default) =>
            Task.FromException<string>(new InvalidOperationException("Start the database first."));

        public (ServerMode Target, ModeOptions Options)? ModeApplied { get; private set; }

        public Task<ModeChangeSet> PreviewModeAsync(ServerMode target, CancellationToken ct = default) =>
            Task.FromResult(new ModeChangeSet(ServerMode.Development, target,
                [new SettingChange("PHP", @"php\8.5.12\conf.d\99-anvil-mode.ini", "display_errors", "On", "Off")],
                [ServiceId.Php, ServiceId.Apache], @"D:\Anvil\data\backups\2026-10-27-120000"));

        public Task<ApplyResult> ApplyModeAsync(ServerMode target, ModeOptions options, CancellationToken ct = default)
        {
            ModeApplied = (target, options);
            return Task.FromResult(ApplyResult.Success() with { RequiredFirewall = FirewallScope.Internet });
        }

        public (ServerMode Mode, ModeProfileOverrides Overrides)? ProfileSaved { get; private set; }

        public Task<IReadOnlyList<ModeProfile>> GetModeProfilesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ModeProfile>>([BuiltInProfiles.Development, BuiltInProfiles.Production]);

        public Task<ApplyResult> SaveModeProfileAsync(ServerMode mode, ModeProfileOverrides overrides, CancellationToken ct = default)
        {
            ProfileSaved = (mode, overrides);
            return Task.FromResult(ApplyResult.Success());
        }

        public Task<IReadOnlyDictionary<string, SiteCertificate>> GetCertificatesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, SiteCertificate>>(new Dictionary<string, SiteCertificate> { ["shop.test"] = Cert });
        public Task ShutdownAsync(bool stopServices, CancellationToken ct = default) => Task.CompletedTask;
    }
}
