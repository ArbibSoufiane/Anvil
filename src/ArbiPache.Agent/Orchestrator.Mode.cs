using ArbiPache.Core.Agent;
using ArbiPache.Core.Apache;
using ArbiPache.Core.Components;
using ArbiPache.Core.Elevation;
using ArbiPache.Core.IO;
using ArbiPache.Core.Modes;
using ArbiPache.Core.Settings;
using Serilog;

namespace ArbiPache.Agent;

/// <summary>
/// Development/Production: a switch backs up every file it writes, writes the mode's PHP, Apache and database files,
/// tests Apache's configuration and restarts only the running services whose files changed. Any failure puts the
/// files and the previous mode back and restarts what had already restarted.
/// </summary>
public sealed partial class Orchestrator
{
    public Task<ModeChangeSet> PreviewModeAsync(ServerMode target, CancellationToken ct) => Locked(() =>
    {
        try
        {
            return Task.FromResult(PlanMode(_settings, NextMode(target, new ModeOptions(KeepXdebug: false))).Set);
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

        ModeChangeSet set;
        IReadOnlyList<string> files;
        try
        {
            (set, files) = PlanMode(previous, next);
        }
        catch (ArgumentException e)
        {
            return ApplyResult.Failure(e.Message);
        }

        var backups = new BackupService(paths, _time);
        var snapshot = backups.Snapshot(files, $"{previous.Mode} to {target}");
        var restarted = new List<ServiceId>();
        try
        {
            // Saved first: the pool restarts and database start below render their files from the current settings.
            Save(next);
            var profile = Profile(next);
            foreach (var phpVersion in PhpVersions(next)) WritePhpMode(phpVersion, profile);
            if (ActiveDb is { } a) WriteDbMode(a.Engine);
            if (InstalledApache(next.ActiveApache) is { } version)
            {
                var applied = await ApplyConfigAsync(next, version, PlanPhp(next), ct);
                if (!applied.Ok)
                {
                    await UndoModeAsync(backups, snapshot, previous, restarted, ct);
                    return ApplyResult.Failure($"Apache rejected the configuration. {previous.Mode} mode stays, and its files were put back.",
                        applied.TestOutput);
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
            return ApplyResult.Failure($"{failed} did not restart in {target} mode, so {previous.Mode} mode and its files were put back. {e.Message}");
        }
        return ApplyResult.Success() with
        {
            RequiredFirewall = target == ServerMode.Production ? FirewallScope.Internet : FirewallScope.LocalNetwork,
        };
    }, ct);

    private AppSettings NextMode(ServerMode target, ModeOptions options) => _settings with
    {
        Mode = target,
        XdebugInProduction = target == ServerMode.Production ? options.KeepXdebug : _settings.XdebugInProduction,
    };

    /// <summary>The change set from one settings to the other, and every file the switch may write.</summary>
    private (ModeChangeSet Set, IReadOnlyList<string> Files) PlanMode(AppSettings from, AppSettings to)
    {
        var versions = PhpVersions(to);
        var xdebug = installer.Installed(ComponentKind.Xdebug);
        var changes = ModePlanner.Changes(new ModePlanInput(Profile(from), Profile(to), versions,
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
