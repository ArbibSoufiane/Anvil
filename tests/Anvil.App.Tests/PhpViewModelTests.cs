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
        _api.Extensions["8.5.12"] = [new("curl", true, true, false), new("intl", true, true, false), new("soap", false, true, false)];
        _api.Extensions["7.4.33"] = [new("curl", true, true, false)];
    }

    private readonly FakeFolderPicker _picker = new();

    private PhpViewModel Make() => new(_api, Paths, new EditorLauncher(() => "auto", new NoRegistry(), _ => false, ""), _picker, ui: a => a());

    private const string PvmFolder = @"C:\Users\me\.pvm\versions\php-8.4.23-Win32-vs17-x64";

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
    public async Task Pvm_versions_Anvil_does_not_have_can_be_added()
    {
        _api.Pvm.Add(new PhpFound("8.5.12", @"C:\Users\me\.pvm\versions\php-8.5.12-nts-Win32-vs17-x64", "vs17", ThreadSafe: false, InAnvil: true));
        _api.Pvm.Add(new PhpFound("8.4.23", PvmFolder, "vs17", ThreadSafe: true, InAnvil: false));
        var vm = Make();
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var offer = Assert.Single(vm.PvmChoices);
        Assert.Equal("PHP 8.4.23 · TS x64 · VS17", offer.Label);
        await offer.AddCommand.ExecuteAsync(null);

        Assert.Equal($"link {PvmFolder}", _api.Calls.Last());
        Assert.Equal("PHP 8.4.23 added from its folder. Anvil keeps its own php.ini for it and leaves the folder as it is.", vm.VersionsMessage);
    }

    [Fact]
    public async Task Without_pvm_the_pvm_list_stays_hidden()
    {
        var vm = Make();

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(vm.HasPvmChoices);
    }

    [Fact]
    public async Task Any_php_folder_can_be_added()
    {
        _picker.Folder = @"C:\devtools\php-7-4-9-for-iis";
        var vm = Make();
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.AddFolderCommand.ExecuteAsync(null);

        Assert.Equal(@"link C:\devtools\php-7-4-9-for-iis", _api.Calls.Last());
    }

    [Fact]
    public async Task A_folder_that_is_not_php_says_why()
    {
        _picker.Folder = @"C:\Temp";
        _api.NextResult = ApplyResult.Failure(@"C:\Temp has no php-cgi.exe, so it is not a PHP folder Anvil can run.");
        var vm = Make();
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.AddFolderCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\Temp has no php-cgi.exe, so it is not a PHP folder Anvil can run.", vm.VersionsMessage);
    }

    [Fact]
    public async Task A_linked_php_is_tagged_and_can_be_removed_from_Anvil()
    {
        _api.Php.Add(new PhpVersionInfo("8.4.23", "vs17", 4, 1000, 0, [], ProcessState.Stopped, IsDefault: false, EndOfLife: false, PvmFolder,
            Linked: true, ThreadSafe: true));
        var vm = Make();
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        var row = vm.Versions.Single(v => v.Version == "8.4.23");

        Assert.Equal(["Linked"], row.Tags);
        Assert.Equal("TS x64 · VS17", row.Build);
        Assert.Equal(Path.Combine(PvmFolder, "ext"), row.ExtPath);
        Assert.False(vm.CanUnlink);
        await row.SelectCommand.ExecuteAsync(null);
        Assert.True(vm.CanUnlink);
        await vm.UnlinkCommand.ExecuteAsync(null);

        Assert.Equal("unlink 8.4.23", _api.Calls.Last());
        Assert.Equal($"PHP 8.4.23 is no longer in Anvil. Its folder {PvmFolder} is unchanged.", vm.ResultText);
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
    public async Task Xdebug_and_opcache_have_their_own_switches()
    {
        _api.Extensions["8.5.12"].AddRange([new("xdebug", false, true, true), new("opcache", true, true, true)]);
        _api.PhpIni["8.5.12"] = new PhpIniValues(new Dictionary<string, string>(), Opcache: true, OpcacheAvailable: true);
        var vm = Make();

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["curl", "intl", "soap"], vm.Extensions.Select(e => e.Name));
        Assert.Equal("Xdebug starts on a trigger: add XDEBUG_TRIGGER to a request, or use a browser extension.", vm.XdebugChip?.Hint);
        Assert.False(vm.XdebugChip!.IsOn);
        Assert.True(vm.OpcacheChip!.IsOn);
        Assert.False(vm.CanGetXdebug);

        await vm.XdebugChip.ToggleCommand.ExecuteAsync(null);
        await vm.OpcacheChip.ToggleCommand.ExecuteAsync(null);

        Assert.Contains("extension 8.5.12 xdebug on", _api.Calls);
        Assert.Contains("opcache 8.5.12 off", _api.Calls);
        Assert.True(vm.XdebugChip.IsOn);
        Assert.False(vm.OpcacheChip.IsOn);
    }

    [Fact]
    public async Task A_thread_safe_php_from_pvm_uses_the_xdebug_in_its_own_folder()
    {
        _api.Php.Add(new PhpVersionInfo("8.3.14", "vs16", 4, 1000, 0, [], ProcessState.Stopped, false, false, PvmFolder, Linked: true, ThreadSafe: true));
        _api.Extensions["8.3.14"] = [new("mcrypt", true, true, false), new("xdebug", true, true, true)];
        _api.Available.Add(Xdebug("8.3"));
        var vm = Make();
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        vm.Versions.Single(v => v.Version == "8.3.14").SelectCommand.Execute(null);
        await vm.XdebugChip!.ToggleCommand.ExecuteAsync(null);

        Assert.Equal(["mcrypt"], vm.Extensions.Select(e => e.Name));
        Assert.Contains("extension 8.3.14 xdebug off", _api.Calls);
        Assert.False(vm.XdebugChip.IsOn);
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
        Assert.Null(vm.XdebugChip);

        await vm.GetXdebugCommand.ExecuteAsync(null);

        Assert.Equal("8.5", _api.Installed.Single(c => c.Kind == ComponentKind.Xdebug).AppliesTo);
        Assert.True(vm.XdebugChip?.IsOn);
        Assert.False(vm.CanGetXdebug);
    }

    [Fact]
    public async Task Php_ini_values_save_only_what_changed()
    {
        _api.PhpIni["8.5.12"] = new PhpIniValues(new Dictionary<string, string> { ["memory_limit"] = "1G", ["post_max_size"] = "256M" }, false, true);
        var vm = Make();
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PhpIniSettings.Editable.Select(s => s.Key), vm.IniFields.Select(f => f.Key));
        Assert.Equal("1G", vm.IniFields[0].Value);
        Assert.Equal("", Field(vm, "max_input_vars").Value);
        Assert.Equal("PHP's default: 1000", Field(vm, "max_input_vars").Hint);

        Field(vm, "max_input_vars").Value = "5000";
        Field(vm, "post_max_size").Value = "";
        await vm.SaveIniCommand.ExecuteAsync(null);

        Assert.Contains("save ini 8.5.12 max_input_vars=5000 post_max_size=", _api.Calls);
        Assert.True(vm.IniOk);
        Assert.Equal("Saved. The pool restarted its workers with the new values.", vm.IniMessage);
        Assert.Equal("5000", Field(vm, "max_input_vars").Value);
        Assert.False(Field(vm, "max_input_vars").IsEdited);
    }

    [Fact]
    public async Task A_php_ini_value_php_would_not_read_is_refused_before_saving()
    {
        var vm = Make();
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Field(vm, "upload_max_filesize").Value = "lots";
        await vm.SaveIniCommand.ExecuteAsync(null);

        Assert.DoesNotContain(_api.Calls, c => c.StartsWith("save ini", StringComparison.Ordinal));
        Assert.False(vm.IniOk);
        Assert.Equal("upload_max_filesize must be a size such as 256M or 1G.", vm.IniMessage);
    }

    [Fact]
    public async Task Listens_on_follows_the_pool_once_apache_starts_it()
    {
        _api.Php[0] = _api.Php[0] with { Port = 0, State = ProcessState.Stopped };
        var vm = Make();
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Not running · starts with Apache", vm.PortText);
        vm.Workers = 6;

        _api.Php[0] = _api.Php[0] with { Port = 9100, State = ProcessState.Running };
        _api.Raise(_api.Status);

        Assert.Equal("127.0.0.1:9100", vm.PortText);
        Assert.Equal("Running", vm.Versions[0].StatusText);
        Assert.Equal(6, vm.Workers);
    }

    private static PhpIniField Field(PhpViewModel vm, string key) => vm.IniFields.Single(f => f.Key == key);

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
