using System.IO.Pipes;
using System.Security.Principal;
using Anvil.Agent;
using Anvil.Core.Agent;
using Anvil.Core.Components;
using Anvil.Core.Paths;
using Anvil.Core.Settings;

namespace Anvil.Core.Tests.Agent;

/// <summary>The Agent under the Windows service: who may connect, and what it reports.</summary>
public sealed class ServiceModeTests : IDisposable
{
    private const string ControlUser = "S-1-5-21-1004336348-1177238915-682003330-1001";

    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ap-svc-" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        try { Directory.Delete(_paths.Root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Pipe_acl_includes_control_user_sid()
    {
        var rules = PipeServer.CreateSecurity([ControlUser]).GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>();

        var rule = Assert.Single(rules, r => r.IdentityReference.Value == ControlUser);
        Assert.True(rule.PipeAccessRights.HasFlag(PipeAccessRights.ReadWrite));
        // Only the Agent's own account may create instances, so a control user cannot serve the pipe name.
        Assert.False(rule.PipeAccessRights.HasFlag(PipeAccessRights.CreateNewInstance));
    }

    [Theory]
    [InlineData(new[] { "--service" }, true)]
    [InlineData(new[] { "--child" }, false)]
    [InlineData(new string[0], false)]
    public void Service_mode_reports_RunningAsService(string[] args, bool expected)
    {
        var options = AgentOptions.FromArgs(args);
        _paths.EnsureCreated();
        var events = new List<string>();
        var orchestrator = new Orchestrator(_paths, new SettingsStore(_paths), new FakeTester(), new FakeApache(events), _ => [],
            new ComponentCatalog([], new ShippedCatalogProvider(_paths)), new ComponentInstaller(_paths, new Downloader(new HttpClient()), _ => null),
            new FakePools(events), _ => true, options.RunningAsService);

        Assert.Equal(AgentPipe.Name, options.PipeName);
        Assert.Equal(AgentPipe.Mutex, options.MutexName);
        Assert.Equal(expected, orchestrator.Status().RunningAsService);
    }
}
