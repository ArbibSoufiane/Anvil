using Anvil.App.Services;
using Anvil.App.Tests.Support;
using Anvil.App.ViewModels;
using Anvil.Core.Agent;
using Anvil.Core.Components;
using Anvil.Core.Paths;
using Anvil.Core.Php;
using Anvil.Core.Processes;

namespace Anvil.App.Tests;

public sealed class PhpViewModelTests
{
    private static readonly AppPaths Paths = new(@"D:\Anvil");
    private readonly FakeAgentApi _api = new();

    public PhpViewModelTests()
    {
        _api.Php.Add(new PhpVersionInfo("8.5.12", "vs17", 4, 1000, 9100, ["htdocs"], ProcessState.Running, IsDefault: true, EndOfLife: false));
        _api.Php.Add(new PhpVersionInfo("7.4.33", "vc15", 2, 1000, 0, [], ProcessState.Stopped, IsDefault: false, EndOfLife: true));
        _api.Extensions["8.5.12"] = [new("curl", true, true, false, false), new("intl", true, true, false, false), new("soap", false, true, false, false)];
        _api.Extensions["7.4.33"] = [new("curl", true, true, false, false)];
    }

    private PhpViewModel Make() => new(_api, Paths, new EditorLauncher(() => "auto", new NoRegistry(), _ => false, ""), ui: a => a());

    private static ComponentRelease Php(string version, string toolset = "vs17") =>
        new(ComponentKind.Php, version, new Uri($"https://downloads.php.net/~windows/releases/php-{version}-nts-Win32-{toolset}-x64.zip"), new string('a', 64), toolset);

    private static ComponentRelease Xdebug(string php) =>
        new(ComponentKind.Xdebug, "3.5.3", new Uri($"https://xdebug.org/files/php_xdebug-3.5.3-{php}-nts-vs17-x86_64.dll"), new string('b', 64), AppliesTo: php);

    [Fact]
    public async Task End_of_life_tag_for_7_4()
    {
        var vm = Make();

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["8.5.12", "7.4.33"], vm.Versions.Select(v => v.Version));
        Assert.Equal(["End of life"], vm.Versions[1].Tags);
        Assert.Equal("NTS x64 · VC15", vm.Versions[1].Build);
        Assert.Equal("none", vm.Versions[1].UsedBy);
        Assert.Equal("Stopped", vm.Versions[1].StatusText);
    }

    [Fact]
    public async Task Default_tag_on_default_version()
    {
        var vm = Make();
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["Default"], vm.Versions[0].Tags);
        Assert.Equal("8.5.12", vm.Selected?.Version);
        Assert.False(vm.MakeDefaultCommand.CanExecute(null));

        vm.Versions[1].SelectCommand.Execute(null);
        Assert.True(vm.MakeDefaultCommand.CanExecute(null));
        await vm.MakeDefaultCommand.ExecuteAsync(null);

        Assert.Contains("default 7.4.33", _api.Calls);
        Assert.Equal(["Default", "End of life"], vm.Versions.Single(v => v.Version == "7.4.33").Tags);
        Assert.Empty(vm.Versions.Single(v => v.Version == "8.5.12").Tags);
        Assert.Equal("7.4.33", vm.Selected?.Version);
    }

    [Fact]
    public async Task Saving_pool_calls_agent_with_values()
    {
        var vm = Make();
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(4, vm.Workers);
        Assert.Equal(1000, vm.MaxRequests);
        Assert.Equal("127.0.0.1:9100", vm.PortText);
        Assert.Equal("300", vm.MaxExecutionTime);
        Assert.Equal("Set by Development profile", vm.MaxExecutionTimeHint);

        vm.Workers = 6;
        vm.MaxRequests = 500;
        await vm.ApplyPoolCommand.ExecuteAsync(null);

        Assert.Contains("pool 8.5.12 6 500", _api.Calls);
        Assert.True(vm.ResultOk);
        Assert.Equal("Saved. The pool restarted its workers with the new settings.", vm.ResultText);
    }

    [Fact]
    public async Task Refused_pool_settings_show_the_reason()
    {
        _api.NextResult = ApplyResult.Failure("Workers must be between 1 and 64.");
        var vm = Make();
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        vm.Workers = 0;
        await vm.ApplyPoolCommand.ExecuteAsync(null);

        Assert.False(vm.ResultOk);
        Assert.Equal("Workers must be between 1 and 64.", vm.ResultText);
    }

    [Fact]
    public async Task Xdebug_chip_disabled_with_mode_hint()
    {
        _api.Installed.Add(new InstalledComponent(ComponentKind.Xdebug, "3.5.3", @"D:\Anvil\runtime\xdebug\3.5.3-php8.5", new string('b', 64),
            DateTimeOffset.Now, AppliesTo: "8.5"));
        var vm = Make();

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var xdebug = vm.Extensions.Single(e => e.Name == "xdebug");
        Assert.True(xdebug.IsLocked);
        Assert.False(xdebug.ToggleCommand.CanExecute(null));
        Assert.Equal("Xdebug follows the mode: loaded in Development, not loaded in Production.", xdebug.Hint);
        Assert.False(vm.CanGetXdebug);
    }

    [Fact]
    public async Task Get_xdebug_installs_the_build_for_this_minor()
    {
        _api.Available.Add(Xdebug("8.4"));
        _api.Available.Add(Xdebug("8.5"));
        var vm = Make();
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(vm.CanGetXdebug);
        Assert.DoesNotContain(vm.Extensions, e => e.Name == "xdebug");

        await vm.GetXdebugCommand.ExecuteAsync(null);

        Assert.Equal("8.5", _api.Installed.Single(c => c.Kind == ComponentKind.Xdebug).AppliesTo);
        Assert.True(vm.Extensions.Single(e => e.Name == "xdebug").IsLocked);
        Assert.False(vm.CanGetXdebug);
    }

    [Fact]
    public async Task Toggling_an_extension_calls_agent_for_that_version()
    {
        var vm = Make();
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.Extensions.Single(e => e.Name == "intl").ToggleCommand.ExecuteAsync(null);

        Assert.Contains("extension 8.5.12 intl off", _api.Calls);
        Assert.False(vm.Extensions.Single(e => e.Name == "intl").IsOn);
        Assert.Equal(["curl", "intl", "soap"], vm.Extensions.Select(e => e.Name));
    }

    [Fact]
    public async Task Refused_extension_change_keeps_the_chip()
    {
        _api.NextResult = ApplyResult.Failure("PHP 8.5.12 has no php_soap.dll in its ext folder.");
        var vm = Make();
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.Extensions.Single(e => e.Name == "soap").ToggleCommand.ExecuteAsync(null);

        Assert.False(vm.Extensions.Single(e => e.Name == "soap").IsOn);
        Assert.Equal("PHP 8.5.12 has no php_soap.dll in its ext folder.", vm.ExtensionsMessage);
    }

    [Fact]
    public async Task Install_choices_offer_newest_patch_of_each_missing_branch()
    {
        _api.Available.AddRange([Php("8.5.12"), Php("8.5.11"), Php("8.4.27"), Php("8.4.26"), Php("7.4.33", "vc15")]);
        _api.Php.RemoveAt(1);
        var vm = Make();
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["PHP 8.4 · 8.4.27", "PHP 7.4 · 7.4.33 · end of life"], vm.InstallChoices.Select(c => c.Label));
        vm.SelectedInstall = vm.InstallChoices[0];
        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Contains("install 8.4.27", _api.Calls);
    }

    [Fact]
    public async Task Rows_open_php_ini_and_ext_folder()
    {
        var vm = Make();

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(@"D:\Anvil\data\php\8.5.12\php.ini", vm.Versions[0].IniPath);
        Assert.Equal(@"D:\Anvil\runtime\php\8.5.12\ext", vm.Versions[0].ExtPath);
    }
}
