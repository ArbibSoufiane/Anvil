using Anvil.Core.Agent;
using Anvil.Core.Apache;
using Anvil.Core.Components;
using Anvil.Core.Database;
using Anvil.Core.Elevation;
using Anvil.Core.IO;
using Anvil.Core.Modes;
using Anvil.Core.Settings;
using Serilog;

namespace Anvil.Agent;

/// <summary>
/// Development/Production: a switch, or an edit of the active mode's profile, backs up every file it writes, writes the
/// mode's PHP, Apache and database files, tests Apache's configuration and restarts only the running services whose
/// files changed. Any failure puts the files, the profile and the previous mode back and restarts what had already restarted.
/// </summary>
public sealed partial class Orchestrator
{
    public Task<ModeChangeSet> PreviewModeAsync(ServerMode target, CancellationToken ct) => Locked(() =>
    {
        try
        {
            var next = NextMode(target, new ModeOptions(KeepXdebug: false));
            return Task.FromResult(PlanMode(_settings, Profile(_settings), next, Profile(next)).Set);
        }
        catch (ArgumentException e)
        {
            throw new InvalidOperationException(e.Message, e);
        }
    }, ct);

    public Task<ApplyResult> ApplyModeAsync(ServerMode target, ModeOptions options, CancellationToken ct) => Locked(async () =>
    {
        var previous = _settings;
        var next = NextMode(target, options);
        if (next == previous) return ApplyResult.Success();

        var result = await ChangeAsync(next, Profile(next), $"{previous.Mode} to {target}", [], () => Save(next),
            $"{previous.Mode} mode stays, and its files were put back.",
            failed => $"{failed} did not restart in {target} mode, so {previous.Mode} mode and its files were put back.", ct);
        return result.Ok
            ? result with { RequiredFirewall = target == ServerMode.Production ? FirewallScope.Internet : FirewallScope.LocalNetwork }
            : result;
    }, ct);

    /// <summary>Development, then Production: the built-in values with the user's edits.</summary>
    public Task<IReadOnlyList<ModeProfile>> GetModeProfilesAsync(CancellationToken ct) => Locked<IReadOnlyList<ModeProfile>>(() =>
        Task.FromResult<IReadOnlyList<ModeProfile>>([Profile(ServerMode.Development), Profile(ServerMode.Production)]), ct);

    /// <summary>Stores the user's edits to a profile; for the active mode, also rewrites its files and restarts what changed.</summary>
    public Task<ApplyResult> SaveModeProfileAsync(ServerMode mode, ModeProfileOverrides overrides, CancellationToken ct) => Locked(async () =>
    {
        var profiles = new ProfileStore(paths);
        ModeProfile edited;
        try
        {
            edited = ProfileStore.With(mode, overrides);
            // The database values are checked now, not at the next switch or database start.
            DbModeRenderer.Values(edited, DbEngine.MariaDb);
        }
        catch (ArgumentException e)
        {
            return ApplyResult.Failure(e.Message);
        }

        if (mode != _settings.Mode)
        {
            profiles.SaveOverrides(mode, overrides);
            return ApplyResult.Success();
        }
        return await ChangeAsync(_settings, WithXdebugChoice(_settings, edited), $"{mode} profile edited", [profiles.FileFor(mode)],
            () => profiles.SaveOverrides(mode, overrides),
            $"The {mode} profile keeps its values, and its files were put back.",
            failed => $"{failed} did not restart with the edited {mode} profile, so its values and files were put back.", ct);
    }, ct);

    /// <summary>
    /// Backs up every file the change may write (and <paramref name="alsoBackUp"/>), commits it, writes the mode files,
    /// applies Apache's configuration and restarts the running services whose files changed.
    /// </summary>
    /// <param name="to">The profile the change leads to, as <see cref="Profile(AppSettings)"/> returns it once committed.</param>
    /// <param name="rejected">Follows "Apache rejected the configuration." when the config test fails.</param>
    /// <param name="notRestarted">The message for a service that did not restart, given the service's name.</param>
    private async Task<ApplyResult> ChangeAsync(AppSettings next, ModeProfile to, string reason, IReadOnlyList<string> alsoBackUp, Action commit,
        string rejected, Func<string, string> notRestarted, CancellationToken ct)
    {
        var previous = _settings;
        ModeChangeSet set;
        IReadOnlyList<string> files;
        try
        {
            (set, files) = PlanMode(previous, Profile(previous), next, to);
        }
        catch (ArgumentException e)
        {
            return ApplyResult.Failure(e.Message);
        }

        var backups = new BackupService(paths, _time);
        var snapshot = backups.Snapshot(files.Concat(alsoBackUp), reason);
        var restarted = new List<ServiceId>();
        try
        {
            // Committed first: the pool restarts and database start below render their files from the current settings and profile.
            commit();
            var profile = Profile(next);
            foreach (var phpVersion in PhpVersions(next)) WritePhpMode(phpVersion, profile);
            if (ActiveDb is { } a) WriteDbMode(a.Engine);
            if (InstalledApache(next.ActiveApache) is { } version)
            {
                var applied = await ApplyConfigAsync(next, version, PlanPhp(next), ct);
                if (!applied.Ok)
                {
                    await UndoModeAsync(backups, snapshot, previous, restarted, ct);
                    return ApplyResult.Failure($"Apache rejected the configuration. {rejected}", applied.TestOutput);
                }
            }
            foreach (var service in set.Restarts)
            {
                restarted.Add(service);
                await RestartForModeAsync(service, ct);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var failed = restarted.Count > 0 ? ServiceName(restarted[^1]) : "The switch";
            await UndoModeAsync(backups, snapshot, previous, restarted, ct);
            return ApplyResult.Failure($"{notRestarted(failed)} {e.Message}");
        }
        return ApplyResult.Success();
    }

    private AppSettings NextMode(ServerMode target, ModeOptions options) => _settings with
    {
        Mode = target,
        XdebugInProduction = target == ServerMode.Production ? options.KeepXdebug : _settings.XdebugInProduction,
    };

    /// <summary>The change set from one settings and profile to the other, and every file the change may write.</summary>
    private (ModeChangeSet Set, IReadOnlyList<string> Files) PlanMode(AppSettings from, ModeProfile fromProfile, AppSettings to, ModeProfile toProfile)
    {
        var versions = PhpVersions(to);
        var xdebug = installer.Installed(ComponentKind.Xdebug);
        var changes = ModePlanner.Changes(new ModePlanInput(fromProfile, toProfile, versions,
            v => PhpModeIniRenderer.XdebugDll(xdebug, v) is not null, ActiveDb?.Engine), paths);

        var restarts = new List<ServiceId>();
        if (php.Running.Count > 0 && changes.Any(c => c.Where == "PHP")) restarts.Add(ServiceId.Php);
        if (ApacheRunning && changes.Any(c => c.Where == "Apache")) restarts.Add(ServiceId.Apache);
        if (ActiveDb is { } a && DbRunning && changes.Any(c => c.Where == DbName(a.Engine))) restarts.Add(ServiceId.Database);

        var files = versions.Select(v => PhpModeIniRenderer.FilePath(paths, v)).ToList();
        if (ActiveDb is { } a2) files.Add(MyIni.ModeFile(a2.Engine));
        if (InstalledApache(to.ActiveApache) is { } version)
            files.AddRange(new ConfigRenderer(paths).Render(RenderInputFor(to, version, PlanPhp(to))).Files.Keys.Select(k => Path.Combine(paths.Conf, k)));

        return (new ModeChangeSet(from.Mode, to.Mode, changes, restarts, new BackupService(paths, _time).NextSnapshot()), files);
    }

    private IReadOnlyList<string> PhpVersions(AppSettings s) => PlanPhp(s).Select(p => p.Version).Distinct().ToList();

    private async Task RestartForModeAsync(ServiceId service, CancellationToken ct)
    {
        switch (service)
        {
            case ServiceId.Php:
                await RestartPoolsAsync(ct);
                break;
            case ServiceId.Apache:
                await apache.GracefulRestartAsync(ct);
                break;
            case ServiceId.Database when ActiveDb is { } a:
                await db!.StopAsync(a.Engine, a.Version, ct);
                await db.StartAsync(a.Engine, a.Version, ct);
                break;
        }
    }

    /// <summary>Puts the snapshot and the previous settings back, then restarts what already restarted with the new files.</summary>
    private async Task UndoModeAsync(BackupService backups, string snapshot, AppSettings previous, IReadOnlyList<ServiceId> restarted,
        CancellationToken ct)
    {
        backups.Restore(snapshot);
        Save(previous);
        foreach (var service in restarted)
        {
            try
            {
                if (service == ServiceId.Database)
                {
                    var started = await StartDbCoreAsync(ct);
                    if (!started.Ok) Log.Error("The database did not start again after the mode switch was undone: {Error}", started.Error);
                }
                else await RestartForModeAsync(service, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.Error(e, "{Service} did not restart after the mode switch was undone", service);
            }
        }
    }

    private string ServiceName(ServiceId id) => id switch
    {
        ServiceId.Php => "The PHP pools",
        ServiceId.Database when ActiveDb is { } a => DbName(a.Engine),
        ServiceId.Database => "The database",
        _ => "Apache",
    };
}
