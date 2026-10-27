using ArbiPache.App.Services;
using ArbiPache.App.Shell;
using ArbiPache.App.Tests.Support;
using ArbiPache.App.ViewModels;
using ArbiPache.Core.Agent;
using ArbiPache.Core.Elevation;
using ArbiPache.Core.Modes;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Settings;
using Avalonia.Headless.XUnit;

namespace ArbiPache.App.Tests;

public sealed class ModeViewModelTests
{
    private const string PhpIni = @"php\8.5.12\conf.d\99-arbipache-mode.ini";
    private static readonly AppPaths Paths = new(@"D:\ArbiPache");
    private readonly FakeAgentApi _api = new();
    private readonly FakeElevator _elevator = new();

    public ModeViewModelTests()
    {
        _api.ModePreview = new ModeChangeSet(ServerMode.Development, ServerMode.Production,
        [
            new SettingChange("PHP", PhpIni, "display_errors", "On", "Off"),
            new SettingChange("PHP", PhpIni, "disable_functions", "", "exec,shell_exec"),
            new SettingChange("Apache", @"conf\mode.conf", "Timeout", "300", "60"),
            new SettingChange("MariaDB", @"mariadb\mode.cnf", "max_statement_time", "0", "60"),
            new SettingChange("Windows Firewall", "", "Web ports open to", "Your network", "The internet"),
        ], [ServiceId.Php, ServiceId.Apache, ServiceId.Database], Path.Combine(Paths.Backups, "2026-10-27-140209"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ShellViewModel Shell() => new(new AppSettings(),
        modeSwitch: (from, to) => new ModeSwitchViewModel(_api, new ElevationFollowUp(_elevator), Paths, from, to));

    private ModeSwitchViewModel ToProduction(bool withService = true) => new(_api, new ElevationFollowUp(_elevator), Paths,
        ServerMode.Development, ServerMode.Production,
        withService ? new ServiceSwitch(_api, new FakeAgentLink(_api.Calls), _elevator, new FakeAgentService(_api.Calls), Paths, "S-1-5-21-1-2-3-1001") : null);

    [Fact]
    public async Task Switching_to_production_also_moves_the_agent_to_the_service()
    {
        var dialog = ToProduction();
        await dialog.LoadAsync(Ct);
        bool? switched = null;
        dialog.Closed += (_, s) => switched = s;

        Assert.True(dialog.OffersService);
        Assert.True(dialog.RunAsService);
        await dialog.ApplyCommand.ExecuteAsync(null);

        Assert.True(switched);
        Assert.Contains(_elevator.Requests, r => r is ServiceInstallRequest);
        Assert.True(_api.Settings.RunAsService);
    }

    [Fact]
    public async Task Leaving_the_service_unticked_only_switches_the_mode()
    {
        var dialog = ToProduction();
        await dialog.LoadAsync(Ct);

        dialog.RunAsService = false;
        await dialog.ApplyCommand.ExecuteAsync(null);

        Assert.DoesNotContain(_elevator.Requests, r => r is ServiceInstallRequest);
        Assert.Equal(ServerMode.Production, _api.ModeApplied?.Target);
    }

    [Fact]
    public async Task The_service_is_not_offered_when_the_agent_already_is_one()
    {
        _api.Settings = _api.Settings with { RunAsService = true };
        var dialog = ToProduction();
        await dialog.LoadAsync(Ct);

        Assert.False(dialog.OffersService);
        Assert.False((await LoadedAsync(ToProduction(withService: false))).OffersService);
    }

    [Fact]
    public async Task A_refused_service_move_is_shown_after_the_switch()
    {
        _elevator.ByVerb["service-install"] = new ElevatedResult(false, "The operation was canceled by the user.");
        var dialog = ToProduction();
        await dialog.LoadAsync(Ct);

        await dialog.ApplyCommand.ExecuteAsync(null);

        Assert.True(dialog.IsDone);
        Assert.Equal("Switched to Production mode. Windows did not let ArbiPache install its service (The operation was canceled by the user.). "
            + "ArbiPache keeps running as before, without the service.", dialog.Message);
    }

    private static async Task<ModeSwitchViewModel> LoadedAsync(ModeSwitchViewModel dialog)
    {
        await dialog.LoadAsync(Ct);
        return dialog;
    }

    private async Task<(ShellViewModel Shell, ModeSwitchViewModel Dialog)> OpenedAsync()
    {
        var shell = Shell();
        await shell.RequestModeCommand.ExecuteAsync(ServerMode.Production);
        return (shell, shell.ModeSwitch!);
    }

    private async Task<ModeViewModel> PageAsync()
    {
        var page = new ModeViewModel(_api, ui: a => a());
        await page.LoadAsync(Ct);
        return page;
    }

    [AvaloniaFact]
    public async Task Header_switch_opens_dialog_with_preview()
    {
        var (shell, dialog) = await OpenedAsync();

        Assert.True(shell.HasModeSwitch);
        Assert.Contains("preview mode Production", _api.Calls);
        Assert.Equal("Development → Production", dialog.Chip);
        Assert.Equal("Switch to Production mode?", dialog.Title);
        Assert.Equal("These changes are written, then ArbiPache restarts the PHP pools, Apache and MariaDB.", dialog.Note);
        Assert.Equal(["PHP", "Apache", "MariaDB", "Windows Firewall"], dialog.Groups.Select(g => g.Name));
        var php = dialog.Groups[0];
        Assert.Equal(PhpIni, php.File);
        Assert.Equal(new ModeChangeRow("display_errors", "On", "Off"), php.Rows[0]);
        Assert.Equal(new ModeChangeRow("disable_functions", "—", "exec,shell_exec"), php.Rows[1]);
        Assert.Equal(@"data\backups\2026-10-27-140209", dialog.BackupPath);
        Assert.True(dialog.ShowsKeepXdebug);
        Assert.True(dialog.ApplyCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Choosing_the_current_mode_opens_nothing()
    {
        var shell = Shell();

        await shell.RequestModeCommand.ExecuteAsync(ServerMode.Development);

        Assert.False(shell.HasModeSwitch);
        Assert.Empty(_api.Calls);
    }

    [AvaloniaFact]
    public async Task Cancel_changes_nothing()
    {
        var (shell, dialog) = await OpenedAsync();

        dialog.CancelCommand.Execute(null);

        Assert.False(shell.HasModeSwitch);
        Assert.Null(_api.ModeApplied);
        Assert.DoesNotContain(_api.Calls, c => c.StartsWith("apply", StringComparison.Ordinal));
        Assert.Empty(_elevator.Requests);
        Assert.Equal(ServerMode.Development, shell.Mode);
    }

    [AvaloniaFact]
    public async Task Keep_xdebug_passed_to_agent()
    {
        var (_, dialog) = await OpenedAsync();
        dialog.KeepXdebug = true;

        await dialog.ApplyCommand.ExecuteAsync(null);

        Assert.Equal((ServerMode.Production, new ModeOptions(KeepXdebug: true)), _api.ModeApplied);
    }

    [AvaloniaFact]
    public async Task Keep_xdebug_is_not_offered_when_switching_to_development()
    {
        var shell = new ShellViewModel(new AppSettings { Mode = ServerMode.Production },
            modeSwitch: (from, to) => new ModeSwitchViewModel(_api, new ElevationFollowUp(_elevator), Paths, from, to));
        await shell.RequestModeCommand.ExecuteAsync(ServerMode.Development);
        var dialog = shell.ModeSwitch!;
        dialog.KeepXdebug = true;

        await dialog.ApplyCommand.ExecuteAsync(null);

        Assert.False(dialog.ShowsKeepXdebug);
        Assert.Equal((ServerMode.Development, new ModeOptions(KeepXdebug: false)), _api.ModeApplied);
    }

    [AvaloniaFact]
    public async Task Firewall_request_sent_after_apply()
    {
        _api.Settings = _api.Settings with { HttpPort = 8080, HttpsPort = 8443 };
        _api.NextResult = ApplyResult.Success() with { RequiredFirewall = FirewallScope.Internet };
        var (shell, dialog) = await OpenedAsync();

        await dialog.ApplyCommand.ExecuteAsync(null);

        var request = Assert.IsType<FirewallSetRequest>(Assert.Single(_elevator.Requests));
        Assert.Equal([8080, 8443], request.Ports);
        Assert.Equal(FirewallScope.Internet, request.Scope);
        Assert.False(shell.HasModeSwitch);
    }

    [AvaloniaFact]
    public async Task A_refused_firewall_change_is_shown_after_the_switch()
    {
        _api.NextResult = ApplyResult.Success() with { RequiredFirewall = FirewallScope.Internet };
        _elevator.Next = new ElevatedResult(false, "The operation was canceled by the user.");
        var (shell, dialog) = await OpenedAsync();

        await dialog.ApplyCommand.ExecuteAsync(null);

        Assert.True(shell.HasModeSwitch);
        Assert.True(dialog.IsDone);
        Assert.Equal("Close", dialog.CancelLabel);
        Assert.StartsWith("Switched to Production mode. Windows did not let ArbiPache open ports 80 and 443", dialog.Message);
        dialog.CancelCommand.Execute(null);
        Assert.False(shell.HasModeSwitch);
    }

    [AvaloniaFact]
    public async Task A_failed_switch_keeps_the_dialog_open_with_the_reason()
    {
        _api.NextResult = ApplyResult.Failure("Apache rejected the configuration. Development mode stays, and its files were put back.");
        var (shell, dialog) = await OpenedAsync();

        await dialog.ApplyCommand.ExecuteAsync(null);

        Assert.True(shell.HasModeSwitch);
        Assert.False(dialog.IsDone);
        Assert.Equal("Apache rejected the configuration. Development mode stays, and its files were put back.", dialog.Message);
        Assert.Empty(_elevator.Requests);
    }

    [AvaloniaFact]
    public async Task Nothing_running_means_nothing_restarts()
    {
        _api.ModePreview = _api.ModePreview! with { Restarts = [] };

        var (_, dialog) = await OpenedAsync();

        Assert.Equal("These changes are written. Nothing that uses them is running, so nothing restarts.", dialog.Note);
    }

    [AvaloniaFact]
    public async Task A_preview_the_agent_refuses_cannot_be_applied()
    {
        _api.ModePreview = null;
        _api.PreviewError = new InvalidOperationException("The Production profile's statement_timeout must be a number of seconds, not \"soon\".");

        var (_, dialog) = await OpenedAsync();

        Assert.Equal("The Production profile's statement_timeout must be a number of seconds, not \"soon\".", dialog.Message);
        Assert.False(dialog.ApplyCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Page_compares_both_profiles()
    {
        var page = await PageAsync();

        Assert.True(page.IsDevelopment);
        var display = page.Rows.Single(r => r.Key == "display_errors");
        Assert.Equal(("PHP", "On", "Off", true), (display.Where, display.Development, display.Production, display.IsDifferent));
        Assert.True(display.DevelopmentActive);
        var disabled = page.Rows.Single(r => r.Key == "disable_functions");
        Assert.Equal("—", disabled.Development);
        Assert.Contains(page.Rows, r => r is { Where: "Apache", Key: "Timeout", Development: "300", Production: "60" });
        Assert.Contains(page.Rows, r => r is { Where: "Database", Key: "statement_timeout", Development: "0", Production: "60" });
        Assert.Contains(page.Rows, r => r is { Where: "PHP", Key: "xdebug", Development: "Loaded", Production: "Not loaded" });
    }

    [AvaloniaFact]
    public async Task Page_cards_ask_for_a_switch_to_the_other_mode()
    {
        var page = await PageAsync();
        var asked = new List<ServerMode>();
        page.SwitchRequested += (_, m) => asked.Add(m);

        page.SwitchToCommand.Execute(ServerMode.Development);
        page.SwitchToCommand.Execute(ServerMode.Production);

        Assert.Equal([ServerMode.Production], asked);
    }

    [AvaloniaFact]
    public async Task Profile_editor_saves_overrides()
    {
        var page = await PageAsync();
        page.EditProfileCommand.Execute(ServerMode.Production);
        var editor = page.Editor!;
        editor.Items.Single(i => i.Key == "memory_limit").Value = "512M";
        editor.SecurityHeaders = false;

        await editor.SaveCommand.ExecuteAsync(null);

        var (mode, overrides) = _api.ProfileSaved!.Value;
        Assert.Equal(ServerMode.Production, mode);
        Assert.Equal(new Dictionary<string, string> { ["memory_limit"] = "512M" }, overrides.Php);
        Assert.Null(overrides.Apache);
        Assert.Null(overrides.Database);
        Assert.False(overrides.SecurityHeaders);
        Assert.Null(overrides.Xdebug);
        Assert.Null(page.Editor);
    }

    [AvaloniaFact]
    public async Task Profile_editor_reset_goes_back_to_built_in_values()
    {
        _api.Profiles[1] = BuiltInProfiles.Production with
        {
            Php = new Dictionary<string, string>(BuiltInProfiles.Production.Php) { ["memory_limit"] = "512M", ["zend.assertions"] = "1" },
        };
        var page = await PageAsync();
        page.EditProfileCommand.Execute(ServerMode.Production);
        var editor = page.Editor!;
        Assert.True(editor.Items.Single(i => i.Key == "memory_limit").IsEdited);

        editor.ResetCommand.Execute(null);
        await editor.SaveCommand.ExecuteAsync(null);

        var overrides = _api.ProfileSaved!.Value.Overrides;
        Assert.Equal(new ModeProfileOverrides(null, null, null, null, null, null), overrides);
    }

    [AvaloniaFact]
    public async Task Profile_editor_shows_the_agents_refusal()
    {
        var page = await PageAsync();
        page.EditProfileCommand.Execute(ServerMode.Development);
        var editor = page.Editor!;
        _api.NextResult = ApplyResult.Failure("Apache rejected the configuration. The Development profile keeps its values, and its files were put back.");
        editor.Items.Single(i => i.Key == "Timeout").Value = "ten";

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Same(editor, page.Editor);
        Assert.Equal("Apache rejected the configuration. The Development profile keeps its values, and its files were put back.", editor.Message);
        Assert.Equal("Save and apply", editor.SaveLabel);
    }
}
