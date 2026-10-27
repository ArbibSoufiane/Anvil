using ArbiPache.Core.Agent;
using ArbiPache.Core.Apache;
using ArbiPache.Core.Components;
using ArbiPache.Core.Database;
using ArbiPache.Core.Php;
using ArbiPache.Core.Settings;
using ArbiPache.Core.Sites;

namespace ArbiPache.Agent;

/// <summary>Switching to another installed version: updates within a branch, put back when the new version does not run.</summary>
public sealed partial class Orchestrator
{
    /// <summary>Newer patch releases of the active Apache, each PHP branch in use and the active database series.</summary>
    public async Task<IReadOnlyList<UpdateOffer>> CheckUpdatesAsync(CancellationToken ct)
    {
        var s = _settings;
        List<ComponentKind> kinds = [ComponentKind.Apache, ComponentKind.Php];
        if (s.DbEngine is { } engine) kinds.Add(DbKind(engine));
        var available = new List<ComponentRelease>();
        foreach (var kind in kinds) available.AddRange(await catalog.GetAsync(kind, ct));
        return ComponentUpdater.Offers(s, PhpInUse(s, new SiteStore(paths).All()), kinds.SelectMany(installer.Installed).ToList(), available);
    }

    /// <summary>
    /// Apache: the new version must pass the config test and answer once started, else the previous one runs again.
    /// PHP: the default, the sites and their pool settings move from older patches of the branch to this one.
    /// Database: a newer release of the active series replaces the running one, which starts again if it fails.
    /// </summary>
    public Task<ApplyResult> ActivateAsync(ComponentKind k, string version, CancellationToken ct) => Locked(() => k switch
    {
        ComponentKind.Apache => ActivateApacheAsync(version, ct),
        ComponentKind.Php => UpdatePhpAsync(version, ct),
        ComponentKind.MariaDb or ComponentKind.MySql => UpdateDbAsync(k == ComponentKind.MariaDb ? DbEngine.MariaDb : DbEngine.MySql, version, ct),
        _ => Task.FromResult(ApplyResult.Failure($"Switching {k} is not available.")),
    }, ct);

    private async Task<ApplyResult> ActivateApacheAsync(string version, CancellationToken ct)
    {
        if (InstalledApache(version) is null) return ApplyResult.Failure($"Apache {version} is not installed.");

        var previous = _settings;
        var next = previous with { ActiveApache = version };
        var bootstrap = new HttpdConfBootstrap(paths);
        bootstrap.EnsureUserConf(version);
        var applied = await ApplyConfigAsync(next, version, PlanPhp(next), ct, alwaysTest: true);
        if (!applied.Ok)
        {
            if (InstalledApache(previous.ActiveApache) is { } old) bootstrap.EnsureUserConf(old);
            return Rejected(applied, $"Apache {previous.ActiveApache ?? "(none)"} stays active.");
        }

        Save(next);
        if (!ApacheRunning) return ApplyResult.Success(applied.ChangedFiles, applied.TestOutput);
        await apache.StopAsync(ct);
        try
        {
            // Returns once the new httpd answers HTTP; throws when it does not.
            await apache.StartAsync(version, next.HttpPort, ct);
            return ApplyResult.Success(applied.ChangedFiles, applied.TestOutput);
        }
        catch (InvalidOperationException e)
        {
            var failed = $"Apache {version} did not start: {e.Message}";
            if (InstalledApache(previous.ActiveApache) is not { } old) return ApplyResult.Failure(failed);
            Save(previous);
            bootstrap.EnsureUserConf(old);
            await ApplyConfigAsync(previous, old, PlanPhp(previous), ct);
            try
            {
                await apache.StartAsync(old, previous.HttpPort, ct);
                return ApplyResult.Failure($"{failed} Apache {old} is running again.");
            }
            catch (InvalidOperationException again)
            {
                return ApplyResult.Failure($"{failed} Apache {old} did not start either: {again.Message}");
            }
        }
    }

    private async Task<ApplyResult> UpdatePhpAsync(string version, CancellationToken ct)
    {
        if (InstalledPhp(version) is null) return ApplyResult.Failure($"PHP {version} is not installed.");
        var store = new SiteStore(paths);
        var sites = store.All();
        var moving = PhpInUse(_settings, sites)
            .Where(v => ComponentUpdater.SameBranch(v, version) && SemVer.Compare(v, version) < 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (moving.Count == 0)
            return ApplyResult.Failure($"PHP {version} is not a newer patch of a PHP version in use. Pick it for a site or as the default instead.");
        var from = string.Join(", ", moving.Order(Comparer<string>.Create(SemVer.Compare)));
        string? Moved(string? v) => v is not null && moving.Contains(v) ? version : v;

        var previous = _settings;
        var tuning = new Dictionary<string, PhpPoolSettings>(previous.PhpPools, StringComparer.OrdinalIgnoreCase);
        foreach (var old in moving.Order(Comparer<string>.Create((a, b) => SemVer.Compare(b, a))))
            if (tuning.Remove(old, out var pool)) tuning.TryAdd(version, pool);
        var next = previous with { DefaultPhp = Moved(previous.DefaultPhp), PhpPools = tuning };

        try
        {
            ComponentUpdater.CarryPhpConfig(paths, version, installer.Installed(ComponentKind.Php).Select(i => i.Version));
            new PhpIniBootstrap(paths).EnsureIni(version);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return ApplyResult.Failure(e.Message);
        }

        var changed = sites.Where(s => moving.Contains(s.PhpVersion ?? "") || s.Mounts.Any(m => moving.Contains(m.PhpVersion ?? ""))).ToList();
        foreach (var site in changed)
            store.Save(site with { PhpVersion = Moved(site.PhpVersion), Mounts = site.Mounts.Select(m => m with { PhpVersion = Moved(m.PhpVersion) }).ToList() });
        void PutSitesBack()
        {
            foreach (var site in changed) store.Save(site);
        }

        if (InstalledApache(next.ActiveApache) is not { } apacheVersion)
        {
            Save(next);
            return ApplyResult.Success();
        }
        var pools = PlanPhp(next);
        var applied = await ApplyConfigAsync(next, apacheVersion, pools, ct);
        if (!applied.Ok)
        {
            PutSitesBack();
            return Rejected(applied, $"PHP {from} stays in use.");
        }

        Save(next);
        if (!ApacheRunning) return ApplyResult.Success(applied.ChangedFiles, applied.TestOutput);
        var started = await Guard(async () =>
        {
            await StartPoolsAsync(pools, ct);
            await apache.GracefulRestartAsync(ct);
        }, applied);
        if (started.Ok) return started;

        PutSitesBack();
        Save(previous);
        var oldPools = PlanPhp(previous);
        await ApplyConfigAsync(previous, apacheVersion, oldPools, ct);
        var restored = await Guard(async () =>
        {
            await StartPoolsAsync(oldPools, ct);
            await apache.GracefulRestartAsync(ct);
        });
        return ApplyResult.Failure(restored.Ok
            ? $"PHP {version} did not start: {started.Error} PHP {from} is running again."
            : $"PHP {version} did not start: {started.Error} PHP {from} did not start either: {restored.Error}");
    }

    private async Task<ApplyResult> UpdateDbAsync(DbEngine engine, string version, CancellationToken ct)
    {
        if (db is null) return ApplyResult.Failure("The database is not available in this Agent.");
        if (ActiveDb is not { } active || active.Engine != engine) return ApplyResult.Failure($"{DbName(engine)} is not the active database.");
        if (!installer.Installed(DbKind(engine)).Any(i => SameVersion(i.Version, version)))
            return ApplyResult.Failure($"Install {DbName(engine)} {version} first.");
        if (SameVersion(active.Version, version)) return ApplyResult.Success();
        if (!ComponentUpdater.SameBranch(active.Version, version))
            return ApplyResult.Failure($"{DbName(engine)} {version} is a new series, not an update of {active.Version}. " +
                "Back up your databases first: a new series upgrades the data files and cannot be undone.");
        if (SemVer.Compare(version, active.Version) < 0)
            return ApplyResult.Failure($"{DbName(engine)} {version} is older than {active.Version}. ArbiPache only moves a database forward.");

        var next = _settings with { DbVersion = version };
        if (!DbRunning)
        {
            Save(next);
            return ApplyResult.Success();
        }
        try
        {
            await db.StopAsync(engine, active.Version, ct);
            await db.StartAsync(engine, version, ct);
            Save(next);
            return ApplyResult.Success();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var failed = $"{DbName(engine)} {version} did not start: {e.Message}";
            try
            {
                await db.StartAsync(engine, active.Version, ct);
                return ApplyResult.Failure($"{failed} {DbName(engine)} {active.Version} is running again.");
            }
            catch (Exception again) when (again is not OperationCanceledException)
            {
                return ApplyResult.Failure($"{failed} {DbName(engine)} {active.Version} did not start either: {again.Message}");
            }
        }
    }

    // Every PHP version a setting or site names, switched on or not: an update moves them all.
    private static IReadOnlyCollection<string> PhpInUse(AppSettings s, IReadOnlyList<SiteDefinition> sites) =>
        sites.SelectMany(site => site.Mounts.Select(m => m.PhpVersion).Prepend(site.PhpVersion))
            .Prepend(s.DefaultPhp)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
