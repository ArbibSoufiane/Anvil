using System.Collections.Concurrent;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Processes;

namespace ArbiPache.Core.Database;

/// <summary>The real database server: <see cref="DatabaseManager"/> under the Agent's process supervisor.</summary>
public sealed class DatabaseServer : IDatabaseServer
{
    private readonly AppPaths _paths;
    private readonly ProcessSupervisor _supervisor;
    private readonly TimeProvider _time;
    private readonly MyIni _ini;
    private readonly DbSecrets _secrets;
    private readonly DatabaseManager _manager;
    private readonly ConcurrentDictionary<(DbEngine, string), IReadOnlyDictionary<string, string>> _defaults = new();

    public DatabaseServer(AppPaths paths, ProcessSupervisor supervisor, TimeProvider time)
    {
        _paths = paths;
        _supervisor = supervisor;
        _time = time;
        _ini = new MyIni(paths);
        _secrets = new DbSecrets(paths);
        _manager = new DatabaseManager(paths, supervisor, _secrets, _ini);
        supervisor.Changed += (_, status) =>
        {
            if (status.Name is "mariadb" or "mysql") StatusChanged?.Invoke(this, status);
        };
    }

    public event EventHandler<ProcessStatus>? StatusChanged;

    public ProcessStatus? Status(DbEngine e) => _supervisor.Snapshot().FirstOrDefault(s => s.Name == DatabaseManager.ProcessName(e));

    public bool Initialized(DbEngine e) => Directory.Exists(_ini.DataDir(e)) && Directory.EnumerateFileSystemEntries(_ini.DataDir(e)).Any();

    public Task InitializeAsync(DbEngine e, string version, string rootPassword, CancellationToken ct) =>
        _manager.InitializeAsync(e, version, rootPassword, ct);

    /// <summary>A server that does not come up is stopped, and its error log's last errors become the message.</summary>
    public async Task StartAsync(DbEngine e, string version, CancellationToken ct)
    {
        _supervisor.Start(_manager.Spec(e, version));
        try
        {
            await _manager.WaitUntilReadyAsync(e, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            await _supervisor.StopAsync(DatabaseManager.ProcessName(e), TimeSpan.FromSeconds(5));
            throw new InvalidOperationException($"{ex.Message} {LastErrors()}".Trim(), ex);
        }
    }

    public Task StopAsync(DbEngine e, string version, CancellationToken ct) => _manager.StopAsync(e, version, ct);

    public async Task<bool> AdoptRootPasswordAsync(DbEngine e, string password, CancellationToken ct)
    {
        if (!await _manager.CanLogInAsync(e, password, ct)) return false;
        _secrets.SaveRootPassword(password);
        return true;
    }

    public Task SetRootPasswordAsync(DbEngine e, string password, CancellationToken ct) => _manager.SetRootPasswordAsync(e, password, ct);

    public async Task<IReadOnlyDictionary<string, string>> DefaultsAsync(DbEngine e, string version, CancellationToken ct)
    {
        if (_defaults.TryGetValue((e, version), out var known)) return known;
        var exe = Path.Combine(_paths.DbRuntime(e, version), "bin", e == DbEngine.MariaDb ? "mariadbd.exe" : "mysqld.exe");
        var probed = await DefaultsProbe.ReadAsync(exe, ct);
        _defaults[(e, version)] = probed;
        return probed;
    }

    public Task<string> BackupAsync(DbEngine e, string version, CancellationToken ct) =>
        new DbBackup(_paths, _secrets, _time).BackupAllAsync(e, version, ct);

    /// <summary>The first [ERROR] lines of the latest start in the error log: the cause comes first, "Aborting" last.</summary>
    private string LastErrors()
    {
        try
        {
            if (!File.Exists(_ini.ErrorLog)) return "";
            using var stream = new FileStream(_ini.ErrorLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var lines = new StreamReader(stream).ReadToEnd().Split('\n').Select(l => l.Trim()).ToList();
            // Both servers log "... as process <pid>" when a start begins.
            var start = lines.FindLastIndex(l => l.Contains(" as process ", StringComparison.Ordinal));
            var errors = lines.Skip(start + 1).Select(l => l.IndexOf("[ERROR] ", StringComparison.Ordinal) is var i and >= 0 ? l[(i + 8)..] : null);
            return string.Join(" ", errors.OfType<string>().Take(2));
        }
        catch (IOException)
        {
            return "";
        }
    }
}
