using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using ArbiPache.Agent;
using ArbiPache.Core.Agent;
using ArbiPache.Core.Apache;
using ArbiPache.Core.Components;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Preflight;
using ArbiPache.Core.Processes;
using ArbiPache.Core.Settings;

namespace ArbiPache.Core.Tests.Agent;

public sealed class RpcTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static string UniquePipe() => "ArbiPache.Agent.Test." + Guid.NewGuid().ToString("N");

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
        Assert.Equal(["arbipache.conf"], saved.ChangedFiles);
        Assert.Equal(8080, fake.Saved?.HttpPort);
        Assert.Equal(ServerMode.Production, fake.Saved?.Mode);
        Assert.Equal([(ServiceId?)null], fake.Started);
        Assert.True(started.Ok);
        Assert.Equal(new ConfigTestResult(true, "Syntax OK"), test);
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
        var options = new AgentOptions(UniquePipe(), @"Local\ArbiPache.Agent.Test." + Guid.NewGuid().ToString("N"), RunningAsService: false);
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

        public AppSettings? Saved { get; private set; }
        public List<ServiceId?> Started { get; } = [];

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
            return Task.FromResult(new ApplyResult(true, null, "Syntax OK", ["arbipache.conf"]));
        }

        public Task<IReadOnlyList<ComponentRelease>> GetAvailableAsync(ComponentKind k, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<InstalledComponent>> GetInstalledAsync(ComponentKind k, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<InstalledComponent> InstallAsync(ComponentRelease r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApplyResult> ActivateAsync(ComponentKind k, string version, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ShutdownAsync(bool stopServices, CancellationToken ct = default) => Task.CompletedTask;
    }
}
