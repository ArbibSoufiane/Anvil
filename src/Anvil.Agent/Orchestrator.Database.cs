using System.Globalization;
using Anvil.Core.Agent;
using Anvil.Core.Components;
using Anvil.Core.Database;
using Anvil.Core.IO;
using Anvil.Core.Processes;

namespace Anvil.Agent;

/// <summary>
/// The database: one engine active at a time, its settings in the user's my.ini. A settings change the server will not
/// start with is put back, and the server runs on as before.
/// </summary>
public sealed partial class Orchestrator
{
    private const string NoDatabase = "Install a database first, on the Database page.";
    private const string NotRunning = "Start the database first.";
    // Any length: the server listens on 127.0.0.1 only, and many local setups use root as root's password.
    private const string NoPassword = "Enter a root password.";
    private readonly DbSettingsCatalog _dbSettings = new();

    private MyIni MyIni => new(paths);

    private (DbEngine Engine, string Version)? ActiveDb =>
        db is not null && _settings is { DbEngine: { } e, DbVersion: { } v } ? (e, v) : null;

    private bool DbRunning =>
        ActiveDb is { } a && db!.Status(a.Engine)?.State is ProcessState.Starting or ProcessState.Running or ProcessState.Restarting;

    private static string DbName(DbEngine e) => e == DbEngine.MariaDb ? "MariaDB" : "MySQL";

    private static ComponentKind DbKind(DbEngine e) => e == DbEngine.MariaDb ? ComponentKind.MariaDb : ComponentKind.MySql;

    private ServiceStatus DbStatus()
    {
        if (ActiveDb is not { } a) return new ServiceStatus(ServiceId.Database, ProcessState.Stopped, null, null);
        var s = db!.Status(a.Engine);
        return new ServiceStatus(ServiceId.Database, s?.State ?? ProcessState.Stopped, $"{DbName(a.Engine)} {a.Version}",
            s?.State == ProcessState.Failed ? s.LastError : null);
    }

    /// <summary>
    /// Makes the installed engine version the active database: a new datadir is initialized with the root password, an
    /// existing one must accept it. The previous engine stops; the new one starts.
    /// </summary>
    public Task<ApplyResult> InstallDatabaseAsync(DbEngine e, string version, string rootPassword, CancellationToken ct) => Locked(async () =>
    {
        if (db is null) return ApplyResult.Failure("The database is not available in this Agent.");
        if (rootPassword.Length == 0) return ApplyResult.Failure(NoPassword);
        if (!installer.Installed(DbKind(e)).Any(i => SameVersion(i.Version, version)))
            return ApplyResult.Failure($"Install {DbName(e)} {version} first.");

        var previous = ActiveDb;
        var same = previous is { } p && p.Engine == e && SameVersion(p.Version, version);
        // Our own running server holds the port and stops below; anything else holding it would stop this one starting.
        if (!DbRunning && !portFree(_settings.DbPort))
            return ApplyResult.Failure($"Port {_settings.DbPort} is already in use by another program. Stop it, or change the database port in Settings.");

        try
        {
            if (previous is { } old && !same && DbRunning) await db.StopAsync(old.Engine, old.Version, ct);
            MyIni.EnsureCreated(e, _settings.DbPort);
            if (!db.Initialized(e))
            {
                await db.InitializeAsync(e, version, rootPassword, ct);
                await db.StartAsync(e, version, ct);
            }
            else
            {
                if (!(same && DbRunning)) await db.StartAsync(e, version, ct);
                if (!await db.AdoptRootPasswordAsync(e, rootPassword, ct))
                {
                    await db.StopAsync(e, version, ct);
                    return ApplyResult.Failure($"That is not the root password of the {DbName(e)} database in {MyIni.DataDir(e)}. " +
                        "Enter the password it was created with.");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ApplyResult.Failure($"{DbName(e)} {version} was not set up. {ex.Message}");
        }

        Save(_settings with { DbEngine = e, DbVersion = version });
        return ApplyResult.Success();
    }, ct);

    /// <summary>Every setting the page manages, with its value in my.ini and the server's own default.</summary>
    public Task<IReadOnlyList<DbSettingRow>> GetDbSettingsAsync(CancellationToken ct) => Locked<IReadOnlyList<DbSettingRow>>(async () =>
    {
        if (ActiveDb is not { } a) return [];
        var defaults = new Dictionary<string, string>(await db!.DefaultsAsync(a.Engine, a.Version, ct), OptionNames.Comparer);
        var ini = MyIni.Read(a.Engine);
        return _dbSettings.For(a.Engine).Select(d =>
        {
            var current = ini.GetValueOrDefault(d.Key);
            var fallback = defaults.GetValueOrDefault(d.Key);
            var changed = current is not null && !SameSetting(d.Kind, current, fallback);
            return new DbSettingRow(d.Key, current, fallback, d.Description, changed, d.RequiresRestart, d.InitOnly);
        }).ToList();
    }, ct);

    /// <summary>Writes the changes into my.ini (null removes the key) and restarts a running server; puts them back if it will not start.</summary>
    public Task<ApplyResult> SaveDbSettingsAsync(IReadOnlyDictionary<string, string?> changes, CancellationToken ct) => Locked(async () =>
    {
        if (ActiveDb is not { } a) return ApplyResult.Failure(NoDatabase);
        var defs = _dbSettings.For(a.Engine).ToDictionary(d => d.Key, OptionNames.Comparer);
        foreach (var (key, value) in changes)
        {
            if (!defs.TryGetValue(key, out var def)) return ApplyResult.Failure($"{key} is not a setting Anvil manages. Edit my.ini for it.");
            if (def.InitOnly) return ApplyResult.Failure($"{key} is fixed when the database is created.");
            // A line break would let a value add its own lines to my.ini.
            if (value is not null && value.Any(char.IsControl)) return ApplyResult.Failure($"{key} cannot contain line breaks.");
        }

        var file = MyIni.PathFor(a.Engine);
        var before = File.ReadAllText(file);
        MyIni.Set(a.Engine, changes);
        if (!DbRunning) return ApplyResult.Success([file]);

        try
        {
            await db!.StopAsync(a.Engine, a.Version, ct);
            await db.StartAsync(a.Engine, a.Version, ct);
            return ApplyResult.Success([file]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AtomicFile.WriteAllText(file, before);
            var restarted = await StartDbCoreAsync(ct);
            return ApplyResult.Failure($"{DbName(a.Engine)} did not start with these settings, so they were put back. {ex.Message}" +
                (restarted.Ok ? "" : $" It did not start with the previous settings either: {restarted.Error}"));
        }
    }, ct);

    public Task<ApplyResult> SetDbRootPasswordAsync(string password, CancellationToken ct) => Locked(async () =>
    {
        if (password.Length == 0) return ApplyResult.Failure(NoPassword);
        if (ActiveDb is not { } a) return ApplyResult.Failure(NoDatabase);
        if (!DbRunning) return ApplyResult.Failure(NotRunning);
        try
        {
            await db!.SetRootPasswordAsync(a.Engine, password, ct);
            return ApplyResult.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ApplyResult.Failure($"The root password was not changed. {ex.Message}");
        }
    }, ct);

    /// <summary>Dumps every database of the running server; returns the .sql.gz path.</summary>
    public Task<string> BackupDatabaseAsync(CancellationToken ct) => Locked(async () =>
    {
        if (ActiveDb is not { } a) throw new InvalidOperationException(NoDatabase);
        if (!DbRunning) throw new InvalidOperationException(NotRunning);
        return await db!.BackupAsync(a.Engine, a.Version, ct);
    }, ct);

    private async Task<ApplyResult> StartDbCoreAsync(CancellationToken ct)
    {
        if (ActiveDb is not { } a) return ApplyResult.Failure(NoDatabase);
        if (DbRunning) return ApplyResult.Success();
        try
        {
            MyIni.EnsureCreated(a.Engine, _settings.DbPort);
            await db!.StartAsync(a.Engine, a.Version, ct);
            return ApplyResult.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ApplyResult.Failure($"{DbName(a.Engine)} did not start. {ex.Message}");
        }
    }

    private async Task<ApplyResult> StopDbCoreAsync(CancellationToken ct)
    {
        if (ActiveDb is not { } a) return ApplyResult.Failure(NoDatabase);
        await db!.StopAsync(a.Engine, a.Version, ct);
        return ApplyResult.Success();
    }

    /// <summary>"ON" equals "TRUE" and "1", and "128M" equals "134217728", so only real differences count as changed.</summary>
    private static bool SameSetting(string kind, string a, string? b) =>
        b is not null && string.Equals(Normalize(kind, a), Normalize(kind, b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string kind, string value)
    {
        value = value.Trim();
        if (kind == "bool")
            return value.ToUpperInvariant() switch { "ON" or "TRUE" or "1" => "1", "OFF" or "FALSE" or "0" => "0", _ => value };
        if (kind != "size" || value.Length == 0) return value;
        var shift = char.ToUpperInvariant(value[^1]) switch { 'K' => 10, 'M' => 20, 'G' => 30, 'T' => 40, _ => 0 };
        var digits = shift == 0 ? value : value[..^1];
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? (n << shift).ToString(CultureInfo.InvariantCulture) : value;
    }
}
