using Anvil.App.Services;
using Anvil.App.Tests.Support;
using Anvil.App.ViewModels;
using Anvil.Core.Agent;
using Anvil.Core.Apache;
using Anvil.Core.Components;
using Anvil.Core.Modes;
using Anvil.Core.Paths;
using Anvil.Core.Processes;
using Anvil.Core.Settings;

namespace Anvil.App.Tests;

public sealed class ApacheViewModelTests
{
    private static readonly AppPaths Paths = new(@"D:\Anvil");
    private readonly FakeAgentApi _api = new();

    private ApacheViewModel Make() => new(_api, Paths, new EditorLauncher(() => "auto", new NoRegistry(), _ => false, ""), ui: a => a());

    private static ComponentRelease Release(string version) =>
        new(ComponentKind.Apache, version, new Uri($"https://www.apachelounge.com/download/VS18/binaries/httpd-{version}.zip"), new string('a', 64), "VS18", "Apache24");

    [Fact]
    public async Task Required_modules_cannot_be_toggled()
    {
        var vm = Make();
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var ssl = vm.Modules.Single(m => m.Name == "ssl");
        Assert.True(ssl.IsRequired);
        Assert.True(ssl.IsOn);
        Assert.False(ssl.ToggleCommand.CanExecute(null));
        var status = vm.Modules.Single(m => m.Name == "status");
        Assert.False(status.IsRequired);
        Assert.False(status.IsOn);
        Assert.True(status.ToggleCommand.CanExecute(null));

        status.ToggleCommand.Execute(null);
        vm.Modules.Single(m => m.Name == "deflate").ToggleCommand.Execute(null);
        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.NotNull(_api.Saved);
        Assert.Equal(["expires", "http2", "status"], _api.Saved.OptionalModules.Order());
        Assert.All(ApacheModules.Required, m => Assert.DoesNotContain(m, _api.Saved.OptionalModules));
    }

    [Fact]
    public async Task Request_timeout_shows_the_users_profile_value()
    {
        _api.Profiles[0] = BuiltInProfiles.Development with
        {
            Apache = new Dictionary<string, string>(BuiltInProfiles.Development.Apache) { ["Timeout"] = "120" },
        };
        var vm = Make();

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("120", vm.RequestTimeout);
    }

    [Fact]
    public async Task Apply_saves_performance_settings()
    {
        _api.Status = _api.Status with { Services = [new ServiceStatus(ServiceId.Apache, ProcessState.Running, "2.4.69", null)] };
        var vm = Make();
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(256, vm.ThreadsPerChild);
        Assert.Equal($"{Environment.ProcessorCount} cores detected · suggested {Suggest.Threads(Environment.ProcessorCount)}", vm.ThreadsHint);
        Assert.Equal("300", vm.RequestTimeout);
        Assert.Equal("Set by Development profile", vm.RequestTimeoutHint);

        vm.ThreadsPerChild = 512;
        vm.KeepAliveTimeout = 15;
        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(new ApacheTuning(512, 0, 15), _api.Saved?.Apache);
        Assert.True(vm.ResultOk);
        Assert.Equal("Saved. Apache reloaded with the new configuration.", vm.ResultText);
    }

    [Fact]
    public async Task Rejected_change_shows_apache_error_text()
    {
        _api.SaveResult = ApplyResult.Failure("Apache rejected the configuration. The settings were not saved.", "AH00526: Syntax error on line 12 of httpd.conf");
        var vm = Make();
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        vm.ThreadsPerChild = 300;
        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.False(vm.ResultOk);
        Assert.Equal("Apache rejected the configuration. The settings were not saved.\nAH00526: Syntax error on line 12 of httpd.conf", vm.ResultText);
        Assert.Equal(256, vm.ThreadsPerChild);
    }

    [Fact]
    public async Task Versions_list_active_installed_and_available()
    {
        _api.Installed.Add(new InstalledComponent(ComponentKind.Apache, "2.4.69", @"D:\Anvil\runtime\apache\2.4.69", new string('a', 64), DateTimeOffset.Now));
        _api.Installed.Add(new InstalledComponent(ComponentKind.Apache, "2.4.66", @"D:\Anvil\runtime\apache\2.4.66", new string('b', 64), DateTimeOffset.Now));
        _api.Available.Add(Release("2.4.70"));
        _api.Available.Add(Release("2.4.69"));
        var vm = Make();

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["2.4.70", "2.4.69", "2.4.66"], vm.Versions.Select(v => v.Version));
        Assert.Equal(["Install", "Active", "Switch"], vm.Versions.Select(v => v.ActionLabel));
        Assert.False(vm.Versions[1].ActionCommand.CanExecute(null));
    }

    [Fact]
    public async Task Installing_first_version_activates_it()
    {
        _api.Settings = _api.Settings with { ActiveApache = null };
        _api.Available.Add(Release("2.4.69"));
        var vm = Make();
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.Versions.Single().ActionCommand.ExecuteAsync(null);

        Assert.Equal(["install 2.4.69", "activate 2.4.69"], _api.Calls);
        Assert.Equal("Active", vm.Versions.Single().ActionLabel);
    }

    [Fact]
    public async Task Test_configuration_shows_result()
    {
        _api.TestResult = new ConfigTestResult(false, "AH00526: Syntax error on line 3");
        var vm = Make();
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.TestConfigCommand.ExecuteAsync(null);

        Assert.False(vm.TestOk);
        Assert.Equal("AH00526: Syntax error on line 3", vm.TestText);
    }

    [Fact]
    public void Config_files_point_into_data()
    {
        var vm = Make();

        Assert.Equal(["httpd.conf", "anvil.conf", "mode.conf", @"vhosts\"], vm.ConfigFiles.Select(f => f.Name));
        Assert.Equal(["Open", "View", "View", "Open folder"], vm.ConfigFiles.Select(f => f.ActionLabel));
        Assert.Equal(@"D:\Anvil\data\apache\httpd.conf", vm.ConfigFiles[0].Path);
        Assert.Equal(@"D:\Anvil\data\conf\vhosts", vm.ConfigFiles[3].Path);
    }
}
